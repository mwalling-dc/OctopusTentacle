using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Octopus.Shared.Contracts
{
    public class VariableDictionary
    {
        readonly IDictionary<string, Variable> variables = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);

        public VariableDictionary() : this(null)
        {
        }

        public VariableDictionary(IEnumerable<Variable> variables)
        {
            if (variables == null)
            {
                return;
            }

            foreach (var variable in variables)
            {
                if (variable == null)
                    continue;

                Set(variable.Name, variable.Value);
            }
        }

        public void Set(string name, string value)
        {
            if (name == null) return;

            var names = SpecialVariables.GetAlternativeNames(name);
            foreach (var alternative in names)
            {
                if (!variables.ContainsKey(alternative))
                {
                    variables[alternative] = new Variable(alternative, value);
                }
                else
                {
                    variables[alternative].Value = value;
                }
            }
        }

        /// <summary>
        /// Performs a case-insensitive lookup of a variable by name, returning null if the variable is not defined.
        /// </summary>
        /// <param name="variableName">Name of the variable.</param>
        /// <returns>The value of the variable, or null if one is not defined.</returns>
        public string GetValue(string variableName)
        {
            foreach (var alternative in SpecialVariables.GetAlternativeNames(variableName))
            {
                Variable variable;
                if (variables.TryGetValue(alternative, out variable) && variable != null)
                    return variable.Value;
            }

            return null;
        }

        public bool GetFlag(string variableName, bool defaultValueIfUnset)
        {
            bool value;
            var text = GetValue(variableName);
            if (string.IsNullOrWhiteSpace(text) || !bool.TryParse(text, out value))
            {
                value = defaultValueIfUnset;
            }
            
            return value;
        }

        public int? GetInt32(string variableName)
        {
            int value;
            var text = GetValue(variableName);
            if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text, out value))
            {
                return null;
            }

            return value;
        }

        public ReadOnlyCollection<Variable> AsList()
        {
            return variables.Select(v => v.Value).ToList().AsReadOnly();
        }

        public IDictionary<string, string> AsDictionary()
        {
            return variables.ToDictionary(v => v.Key, v => v.Value.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}