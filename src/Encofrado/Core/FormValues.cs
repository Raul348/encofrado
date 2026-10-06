using System;
using System.Collections.Generic;
using System.Globalization;

namespace Encofrado.Core
{
    /// <summary>Valores capturados en el formulario (independientes de la ventana).</summary>
    public sealed class FormValues
    {
        private readonly Dictionary<string, object> _v;

        public FormValues(Dictionary<string, object> values) { _v = values; }

        public bool Has(string key) => _v.ContainsKey(key);

        /// <summary>Número tal como se escribió (normalmente en cm).</summary>
        public double D(string key) => Convert.ToDouble(_v[key], CultureInfo.InvariantCulture);

        /// <summary>Número escrito en centímetros convertido a pies.</summary>
        public double Cm(string key) => Un.Cm(D(key));

        public int I(string key) => (int)Math.Round(D(key));
        public bool B(string key) => _v.TryGetValue(key, out object o) && o is bool b && b;
        public string S(string key) => _v.TryGetValue(key, out object o) ? o as string ?? "" : "";
    }
}
