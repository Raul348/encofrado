using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Encofrado.Core;
using Encofrado.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace Encofrado.Commands
{
    /// <summary>Flujo común: seleccionar → formulario con figura → generar tableros → resumen de áreas.</summary>
    internal static class Runner
    {
        // ------------------------------------------------------------------ Un tipo de elemento

        public static Result PorPieza(UIApplication uiapp, Pieza p, ref string message)
        {
            UIDocument uidoc = Proyecto(uiapp);
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<Element> hosts = Seleccionar(uidoc, p);
            if (hosts.Count == 0) return Result.Cancelled;

            Medidas med = Medidas.De(hosts[0], p);
            var (latB, fonB) = Constructor.AreaBruta(hosts[0], p);
            double lat = Almacen.M2(latB), fon = Almacen.M2(fonB);

            var form = new ParamForm(App.Name, "enc_" + Piezas.Nombre(p).ToLowerInvariant(),
                "Encofrado de " + Piezas.Plural(p).ToUpperInvariant(),
                $"{hosts.Count} elemento(s) seleccionado(s)   ·   {Descripcion(p, med)}");
            form.Section("Caras a encofrar");
            form.Chk("lat", Piezas.NombreCara(p, Cara.Lateral), Piezas.LateralPorDefecto(p));
            if (Piezas.TieneFondo(p)) form.Chk("fondo", Piezas.NombreCara(p, Cara.Fondo), Piezas.FondoPorDefecto(p));
            Opciones(form, p == Pieza.Columna || p == Pieza.Viga || p == Pieza.Zapata, p == Pieza.Muro);
            form.Figure = (g, r, v) => Figuras.Dibujar(g, r, v, p, med, lat, fon, hosts.Count);
            form.Validator = v => v.B("lat") || (v.Has("fondo") && v.B("fondo")) ? null : "Elija al menos una cara a encofrar.";
            form.AcceptText = "Generar encofrado";

            FormValues values;
            using (form)
            {
                if (form.ShowDialog(new RevitWindow(uiapp.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                values = form.Values();
            }

            var opciones = new Dictionary<Pieza, Opciones> { [p] = Leer(values, p, values.B("lat"), values.Has("fondo") && values.B("fondo")) };
            return Procesar(uiapp, hosts.Select(h => (h, p)).ToList(), opciones, values, "ENCOFRADO - " + Piezas.Plural(p), ref message);
        }

        // ------------------------------------------------------------------ Toda la edificación

        public static Result Todo(UIApplication uiapp, ref string message)
        {
            UIDocument uidoc = Proyecto(uiapp);
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            Dictionary<Pieza, List<Element>> enVista = Recolectar(doc, uidoc.ActiveView);
            Dictionary<Pieza, List<Element>> enProyecto = Recolectar(doc, null);

            var form = new ParamForm(App.Name, "enc_todo", "Encofrado de TODA LA EDIFICACIÓN",
                $"Vista activa: {uidoc.ActiveView.Name}");
            form.Section("Elementos");
            foreach (Pieza p in Piezas.Todas) form.Chk("p_" + p, Piezas.Plural(p), true);
            form.Section("Alcance");
            form.Choice("alcance", "Buscar elementos en", new[] { "La vista activa (lo visible)", "Todo el proyecto" }, 0);
            Opciones(form, true, true);
            form.Note("Cada elemento usa sus caras habituales: columnas y zapatas sus costados; vigas, losas, muros y escaleras también su fondo.");
            form.Figure = (g, r, v) => Figuras.Todo(g, r, v, p => (v.I("alcance") == 1 ? enProyecto : enVista)[p].Count);
            form.Validator = v => Piezas.Todas.Any(p => v.B("p_" + p)) ? null : "Elija al menos un tipo de elemento.";
            form.AcceptText = "Encofrar todo";

            FormValues values;
            using (form)
            {
                if (form.ShowDialog(new RevitWindow(uiapp.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                values = form.Values();
            }

            var fuente = values.I("alcance") == 1 ? enProyecto : enVista;
            var items = new List<(Element, Pieza)>();
            var opciones = new Dictionary<Pieza, Opciones>();
            foreach (Pieza p in Piezas.Todas)
            {
                if (!values.B("p_" + p)) continue;
                opciones[p] = Leer(values, p, true, Piezas.FondoPorDefecto(p));
                items.AddRange(fuente[p].Select(e => (e, p)));
            }
            if (items.Count == 0)
            {
                TaskDialog.Show(App.Name, "No se encontraron elementos para encofrar.");
                return Result.Cancelled;
            }
            return Procesar(uiapp, items, opciones, values, "ENCOFRADO - Edificación", ref message);
        }

        // ------------------------------------------------------------------ Cortar intersecciones

        public static Result Cortar(UIApplication uiapp, ref string message)
        {
            UIDocument uidoc = Proyecto(uiapp);
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<DirectShape> todas = Almacen.Todos(doc).ToList();
            if (todas.Count == 0)
            {
                TaskDialog.Show(App.Name, "Todavía no hay encofrado en el proyecto. Genere primero el encofrado de los elementos.");
                return Result.Cancelled;
            }

            // Selección previa: piezas de encofrado o elementos estructurales (se toma su encofrado).
            var elegidas = new HashSet<ElementId>();
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element e = doc.GetElement(id);
                if (e is DirectShape ds && ds.ApplicationId == Almacen.AppId) elegidas.Add(id);
                else if (e != null && Piezas.De(e).HasValue) foreach (ElementId x in Almacen.DeElemento(doc, e)) elegidas.Add(x);
            }

            var form = new ParamForm(App.Name, "enc_cortar", "CORTAR INTERSECCIONES de encofrado",
                $"{todas.Count} pieza(s) de encofrado en el proyecto" + (elegidas.Count > 0 ? $" · {elegidas.Count} seleccionada(s)" : ""));
            form.Section("Prioridad");
            form.Choice("orden", "Queda completo primero", Cortador.Ordenes.Select(Cortador.Texto).ToArray(), 0);
            form.Note("El encofrado de mayor prioridad queda entero; al de menor prioridad se le recorta la parte que se superpone.");
            form.Section("Alcance");
            form.Choice("alcance", "Piezas a revisar",
                new[] { "Todo el encofrado del proyecto", "Solo el encofrado seleccionado" }, elegidas.Count > 0 ? 1 : 0);
            form.Chk("mismo", "Cortar también entre piezas del mismo tipo (vigas que se cruzan, muros en L o T)", true);
            form.Figure = (g, r, v) => Figuras.Cortar(g, r, v);
            form.Validator = v => v.I("alcance") == 1 && elegidas.Count == 0
                ? "No hay encofrado seleccionado. Seleccione piezas de encofrado o elementos estructurales, o elija todo el proyecto."
                : null;
            form.AcceptText = "Cortar intersecciones";

            FormValues values;
            using (form)
            {
                if (form.ShowDialog(new RevitWindow(uiapp.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                values = form.Values();
            }

            List<DirectShape> piezas = values.I("alcance") == 1
                ? todas.Where(d => elegidas.Contains(d.Id)).ToList()
                : todas;

            Cortador.Resultado res;
            using (var t = new Transaction(doc, "ENCOFRADO - Cortar intersecciones"))
            {
                t.Start();
                FailureHandlingOptions fo = t.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(new SinAdvertencias());
                t.SetFailureHandlingOptions(fo);
                res = Cortador.Cortar(doc, piezas, Cortador.Ordenes[Math.Max(0, values.I("orden"))], values.B("mismo"));
                t.Commit();
            }

            var td = new TaskDialog(App.Name)
            {
                MainInstruction = res.Cortadas + res.Eliminadas > 0
                    ? $"Se cortaron {res.Cortadas} pieza(s): {Almacen.M2(res.AreaQuitada):N2} m² superpuestos descontados"
                    : "No se encontraron intersecciones entre las piezas de encofrado.",
                MainContent = $"Piezas revisadas: {res.Revisadas}\n" +
                              (res.Eliminadas > 0 ? $"Piezas eliminadas (quedaban totalmente dentro de otra): {res.Eliminadas}\n" : "") +
                              "El área ENC_Area de las piezas cortadas ya está actualizada para el metrado."
            };
            if (res.Errores.Count > 0) td.ExpandedContent = "Errores:\n" + string.Join("\n", res.Errores.Take(20));
            td.Show();
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ Seleccionar encofrado por tipo

        public static Result Seleccionar(UIApplication uiapp, Pieza? p)
        {
            UIDocument uidoc = Proyecto(uiapp);
            if (uidoc == null) return Result.Cancelled;
            List<ElementId> ids = Almacen.Todos(uidoc.Document)
                .Where(d => !p.HasValue || Almacen.PiezaDe(d) == p)
                .Select(d => d.Id).ToList();
            if (ids.Count == 0)
            {
                TaskDialog.Show(App.Name, p.HasValue
                    ? $"No hay encofrado de {Piezas.Plural(p.Value).ToLowerInvariant()} en el proyecto."
                    : "No hay encofrado en el proyecto.");
                return Result.Cancelled;
            }
            uidoc.Selection.SetElementIds(ids);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ Común

        private static void Opciones(ParamForm form, bool concreto, bool muros)
        {
            form.Section("Opciones");
            form.Num("esp", "Espesor del tablero", 1.8, "cm", 1, 0.5, 10);
            form.Chk("desc", "Descontar contacto con otros elementos", true);
            form.Chk("reemp", "Reemplazar el encofrado existente de estos elementos", true);
            form.Chk("cortar", "Cortar intersecciones entre encofrados al terminar", true);
            if (concreto) form.Chk("conc", "Solo concreto vaciado en obra (omite acero, madera, prefabricado)", true);
            if (muros) form.Chk("estr", "Solo muros estructurales (omite tabiquería)", true);
        }

        private static Opciones Leer(FormValues v, Pieza p, bool lat, bool fondo) => new Opciones
        {
            Lateral = lat,
            Fondo = fondo && Piezas.TieneFondo(p),
            OmitirBase = Piezas.OmitirBase(p),
            Descontar = v.B("desc"),
            Espesor = v.Cm("esp")
        };

        private static string Descripcion(Pieza p, Medidas m)
        {
            switch (p)
            {
                case Pieza.Columna: return $"Sección {m.A:0.#} × {m.B:0.#} cm · Altura {m.C / 100:0.00} m";
                case Pieza.Viga: return $"Sección {m.A:0.#} × {m.B:0.#} cm · Luz {m.C / 100:0.00} m";
                case Pieza.Zapata: return $"{m.A / 100:0.00} × {m.B / 100:0.00} m · Peralte {m.C:0.#} cm";
                case Pieza.Losa: return $"{m.A / 100:0.00} × {m.B / 100:0.00} m · Espesor {m.C:0.#} cm";
                case Pieza.Muro: return $"Longitud {m.A / 100:0.00} m · Espesor {m.B:0.#} cm · Altura {m.C / 100:0.00} m";
                default: return $"Longitud {m.A / 100:0.00} m · Altura {m.C / 100:0.00} m";
            }
        }

        private static UIDocument Proyecto(UIApplication uiapp)
        {
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null || uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show(App.Name, "Abra un proyecto de Revit (no una familia).");
                return null;
            }
            return uidoc;
        }

        private static Dictionary<Pieza, List<Element>> Recolectar(Document doc, View view)
        {
            var col = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
            var result = Piezas.Todas.ToDictionary(p => p, p => new List<Element>());
            foreach (Element e in col.WhereElementIsNotElementType())
            {
                Pieza? p = Piezas.De(e);
                if (p.HasValue) result[p.Value].Add(e);
            }
            return result;
        }

        private static List<Element> Seleccionar(UIDocument uidoc, Pieza p)
        {
            Document doc = uidoc.Document;
            List<Element> pre = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id))
                .Where(e => e != null && Piezas.Es(p, e)).ToList();
            if (pre.Count > 0) return pre;
            try
            {
                return uidoc.Selection.PickObjects(ObjectType.Element, new Filtro(p),
                        $"Seleccione {Piezas.Plural(p).ToLowerInvariant()} a encofrar y pulse Finalizar")
                    .Select(r => doc.GetElement(r)).Where(e => e != null).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<Element>();
            }
        }

        private static Result Procesar(UIApplication uiapp, List<(Element host, Pieza p)> items, Dictionary<Pieza, Opciones> opciones,
            FormValues v, string titulo, ref string message)
        {
            Document doc = uiapp.ActiveUIDocument.Document;
            var rep = new Reporte();
            Cortador.Resultado corte = null;
            bool reemplazar = v.B("reemp"), conc = v.B("conc"), estr = v.B("estr");

            using (var t = new Transaction(doc, titulo))
            {
                t.Start();
                FailureHandlingOptions fo = t.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(new SinAdvertencias());
                t.SetFailureHandlingOptions(fo);

                Almacen.AsegurarParametros(doc, uiapp.Application);
                var materiales = new Dictionary<Pieza, ElementId>();
                var vecinos = new Vecinos(doc);
                var creadas = new List<(DirectShape ds, Pieza p, Cara cara)>();

                foreach (var (host, p) in items)
                {
                    string excl = Piezas.Excluir(p, host, conc, estr);
                    if (excl != null) { rep.Omitidos.Add($"{host.Name} [Id {host.Id}]: {excl}"); continue; }
                    if (!materiales.ContainsKey(p)) materiales[p] = Almacen.Material(doc, p);

                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        var nuevas = new List<(DirectShape, Pieza, Cara)>();
                        try
                        {
                            if (reemplazar)
                            {
                                List<ElementId> viejos = Almacen.DeElemento(doc, host);
                                if (viejos.Count > 0) doc.Delete(viejos);
                            }
                            List<Tablero> tabs = Constructor.Construir(doc, host, opciones[p], vecinos, materiales[p], rep.Avisos);
                            foreach (var grupo in tabs.GroupBy(x => x.Cara))
                            {
                                nuevas.Add((Almacen.Crear(doc, host, p, grupo.Key, grupo.ToList()), p, grupo.Key));
                                rep.Tableros += grupo.Count();
                            }
                            st.Commit();
                            creadas.AddRange(nuevas);
                            rep.Elementos++;
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();
                            rep.Errores.Add($"{host.Name} [Id {host.Id}]: {ex.Message}");
                        }
                    }
                }

                // Cortar las intersecciones con el resto del encofrado (prioridad por defecto).
                if (rep.Elementos > 0 && v.B("cortar"))
                {
                    doc.Regenerate();
                    corte = Cortador.Cortar(doc, Almacen.Todos(doc).ToList(), Cortador.Ordenes[0], true);
                }

                foreach (var (ds, p, cara) in creadas)
                    if (ds.IsValidObject) rep.Sumar(p, cara, Almacen.Area(ds));

                if (rep.Elementos > 0) t.Commit(); else t.RollBack();
            }

            var td = new TaskDialog(App.Name)
            {
                MainInstruction = rep.Elementos > 0
                    ? $"Encofrado generado en {rep.Elementos} elemento(s): {rep.TotalM2:N2} m²"
                    : "No se generó encofrado.",
                MainContent = (rep.Areas.Count > 0 ? rep.Tabla() : "") +
                              (corte != null && corte.Cortadas + corte.Eliminadas > 0
                                  ? $"\nIntersecciones cortadas: {corte.Cortadas + corte.Eliminadas} pieza(s), {Almacen.M2(corte.AreaQuitada):N2} m² superpuestos descontados."
                                  : "") +
                              (rep.Areas.Count > 0 ? "\nUse \"Metrado\" para ver la tabla y exportar a Excel (CSV)." : ""),
            };
            var extra = new List<string>();
            if (rep.Errores.Count > 0) extra.Add("Errores:\n" + string.Join("\n", rep.Errores.Take(20)));
            if (rep.Omitidos.Count > 0) extra.Add($"Omitidos ({rep.Omitidos.Count}):\n" + string.Join("\n", rep.Omitidos.Take(20)));
            if (rep.Avisos.Count > 0) extra.Add("Avisos:\n" + string.Join("\n", rep.Avisos.Take(20)));
            if (extra.Count > 0) td.ExpandedContent = string.Join("\n\n", extra);
            td.Show();

            if (rep.Elementos == 0)
            {
                message = rep.Errores.FirstOrDefault() ?? rep.Omitidos.FirstOrDefault() ?? "No se generó encofrado.";
                return rep.Errores.Count > 0 ? Result.Failed : Result.Cancelled;
            }
            return Result.Succeeded;
        }

        private sealed class Filtro : ISelectionFilter
        {
            private readonly Pieza _p;
            public Filtro(Pieza p) { _p = p; }
            public bool AllowElement(Element e) => Piezas.Es(_p, e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        private sealed class SinAdvertencias : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
