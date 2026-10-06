using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Encofrado.Core
{
    /// <summary>
    /// Corta las intersecciones entre piezas de encofrado: donde dos tableros ocupan el mismo espacio
    /// (esquina columna-viga, viga-losa, muros en L o T, etc.) el de mayor prioridad queda completo y al
    /// de menor prioridad se le resta la parte superpuesta. El área se recalcula en proporción al volumen.
    /// </summary>
    internal static class Cortador
    {
        /// <summary>Órdenes de prioridad disponibles (el primero queda completo).</summary>
        public static readonly Pieza[][] Ordenes =
        {
            new[] { Pieza.Columna, Pieza.Muro, Pieza.Viga, Pieza.Losa, Pieza.Escalera, Pieza.Zapata },
            new[] { Pieza.Losa, Pieza.Viga, Pieza.Columna, Pieza.Muro, Pieza.Escalera, Pieza.Zapata },
            new[] { Pieza.Viga, Pieza.Columna, Pieza.Muro, Pieza.Losa, Pieza.Escalera, Pieza.Zapata },
        };

        public static string Texto(Pieza[] orden) => string.Join(" → ", orden.Select(Piezas.Plural));

        internal sealed class Resultado
        {
            public int Revisadas;
            public int Cortadas;
            public int Eliminadas;
            public double AreaQuitada; // pies²
            public readonly List<string> Errores = new List<string>();
        }

        private sealed class Pieza_
        {
            public DirectShape Ds;
            public int Rango;
            public List<Solid> Solidos;
            public BoundingBoxXYZ Caja;
            public double Volumen0;
            public bool Cambiada;
        }

        public static Resultado Cortar(Document doc, IEnumerable<DirectShape> piezas, Pieza[] orden, bool mismoTipo)
        {
            var res = new Resultado();
            var lista = new List<Pieza_>();
            foreach (DirectShape ds in piezas)
            {
                Pieza? p = Almacen.PiezaDe(ds);
                if (!p.HasValue) continue;
                List<Solid> sol = Geo.Solids(ds).Select(SolidUtils.Clone).ToList();
                if (sol.Count == 0) continue;
                lista.Add(new Pieza_
                {
                    Ds = ds,
                    Rango = Array.IndexOf(orden, p.Value),
                    Solidos = sol,
                    Caja = ds.get_BoundingBox(null),
                    Volumen0 = sol.Sum(s => s.Volume)
                });
            }
            res.Revisadas = lista.Count;

            // De mayor a menor prioridad: cuando se recorta una pieza, las de mayor prioridad ya están definitivas.
            lista = lista.OrderBy(x => x.Rango).ThenBy(x => x.Ds.Id.Value).ToList();
            for (int i = 0; i < lista.Count; i++)
            {
                Pieza_ baja = lista[i];
                for (int j = 0; j < i; j++)
                {
                    Pieza_ alta = lista[j];
                    if (alta.Rango == baja.Rango && !mismoTipo) continue;
                    if (alta.Ds.ApplicationDataId == baja.Ds.ApplicationDataId) continue; // mismo elemento
                    if (!Tocan(alta.Caja, baja.Caja)) continue;

                    for (int k = 0; k < baja.Solidos.Count; k++)
                    {
                        Solid s = baja.Solidos[k];
                        foreach (Solid h in alta.Solidos)
                        {
                            try
                            {
                                Solid d = BooleanOperationsUtils.ExecuteBooleanOperation(s, h, BooleanOperationsType.Difference);
                                if (d != null && d.Volume < s.Volume - 1e-9)
                                {
                                    s = d;
                                    baja.Cambiada = true;
                                }
                            }
                            catch (Exception) { /* geometría degenerada: se deja como está */ }
                        }
                        baja.Solidos[k] = s;
                    }
                }
            }

            foreach (Pieza_ x in lista.Where(x => x.Cambiada))
            {
                try
                {
                    List<Solid> quedan = x.Solidos.Where(s => s.Volume > 1e-9).ToList();
                    double area0 = Almacen.Area(x.Ds);
                    double vol = quedan.Sum(s => s.Volume);
                    double area = x.Volumen0 > 0 ? area0 * vol / x.Volumen0 : 0;
                    res.AreaQuitada += area0 - area;
                    if (quedan.Count == 0)
                    {
                        doc.Delete(x.Ds.Id);
                        res.Eliminadas++;
                        continue;
                    }
                    x.Ds.SetShape(quedan.Cast<GeometryObject>().ToList());
                    Almacen.FijarArea(x.Ds, area, "cortado");
                    res.Cortadas++;
                }
                catch (Exception ex)
                {
                    res.Errores.Add($"Pieza {x.Ds.Id}: {ex.Message}");
                }
            }
            return res;
        }

        private static bool Tocan(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return true;
            double t = Un.Mm(1);
            return a.Min.X <= b.Max.X + t && b.Min.X <= a.Max.X + t
                && a.Min.Y <= b.Max.Y + t && b.Min.Y <= a.Max.Y + t
                && a.Min.Z <= b.Max.Z + t && b.Min.Z <= a.Max.Z + t;
        }
    }
}
