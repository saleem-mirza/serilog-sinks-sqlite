using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Newtonsoft.Json;
using Serilog.Events;

namespace Serilog.Extensions
{
    public static class LogEventExtensions
    {
        public static string Json(this LogEvent logEvent, bool storeTimestampInUtc = false)
        {
            return JsonConvert.SerializeObject(ConvertToObject(logEvent, storeTimestampInUtc));
        }

        public static dynamic Object(this LogEvent logEvent, bool storeTimestampInUtc = false)
        {
            return ConvertToObject(logEvent, storeTimestampInUtc);
        }

        public static string Json(this IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            return JsonConvert.SerializeObject(ConvertToObject(properties));
        }

        public static dynamic Object(this IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            return ConvertToObject(properties);
        }

        #region Private implementation

        private static dynamic ConvertToObject(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            var expObject = new ExpandoObject() as IDictionary<string, object>;
            foreach (var property in properties)
            {
                expObject.Add(property.Key, Simplify(property.Value));
            }
            return expObject;
        }

        private static dynamic ConvertToObject(LogEvent logEvent, bool storeTimestampInUtc)
        {
            var eventObject = new
            {
                Timestamp =
                storeTimestampInUtc
                    ? logEvent.Timestamp.ToUniversalTime().ToString("o")
                    : logEvent.Timestamp.ToString("o"),
                Level = logEvent.Level.ToString(),
                MessageTemplate = logEvent.MessageTemplate.ToString(),
                logEvent.Exception,
                Properties = logEvent.Properties.Object()
            };
            return eventObject;
        }

        private static object Simplify(LogEventPropertyValue data)
        {
            var value = data as ScalarValue;
            if (value != null)
                return value.Value;

            var dictValue = data as IReadOnlyDictionary<string, LogEventPropertyValue>;
            if (dictValue != null)
            {
                var expObject = new ExpandoObject() as IDictionary<string, object>;
                foreach (var item in dictValue.Keys)
                    expObject.Add(item, Simplify(dictValue[item]));
                return expObject;
            }

            var seq = data as SequenceValue;
            if (seq != null)
                return seq.Elements.Select(Simplify).ToArray();

            var str = data as StructureValue;
            if (str == null) return null;
            {
                try
                {
                    if (str.TypeTag == null)
                        return str.Properties.ToDictionary(p => p.Name, p => Simplify(p.Value));

                    if (!str.TypeTag.StartsWith("DictionaryEntry") && !str.TypeTag.StartsWith("KeyValuePair"))
                        return str.Properties.ToDictionary(p => p.Name, p => Simplify(p.Value));

                    var key = Simplify(str.Properties[0].Value);
                    if (key == null)
                        return null;

                    var expObject = new ExpandoObject() as IDictionary<string, object>;
                    expObject.Add(key.ToString(), Simplify(str.Properties[1].Value));
                    return expObject;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }

            return null;
        }

        #endregion
    }
}