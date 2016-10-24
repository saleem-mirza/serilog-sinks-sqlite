using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Newtonsoft.Json;
using Serilog.Events;

namespace Serilog.Sinks.Extensions
{
    public static class LogEventExtensions
    {
        public static string Json(this LogEvent logEvent, bool storeTimestampInUtc = false)
        {
            return JsonConvert.SerializeObject(ConvertToDictionary(logEvent, storeTimestampInUtc));
        }

        public static IDictionary<string, object> Dictionary(this LogEvent logEvent, bool storeTimestampInUtc = false)
        {
            return ConvertToDictionary(logEvent, storeTimestampInUtc);
        }

        public static string Json(this IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            return JsonConvert.SerializeObject(ConvertToDictionary(properties));
        }

        public static IDictionary<string, object> Dictionary(
            this IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            return ConvertToDictionary(properties);
        }

        #region Private implementation

        private static dynamic ConvertToDictionary(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            var expObject = new ExpandoObject() as IDictionary<string, object>;
            foreach (var property in properties)
                expObject.Add(property.Key, Simplify(property.Value));
            return expObject;
        }

        private static dynamic ConvertToDictionary(LogEvent logEvent, bool storeTimestampInUtc)
        {
            var eventObject = new ExpandoObject() as IDictionary<string, object>;
            eventObject.Add("Timestamp", storeTimestampInUtc
                ? logEvent.Timestamp.ToUniversalTime().ToString("o")
                : logEvent.Timestamp.ToString("o"));

            eventObject.Add("Level", logEvent.Level.ToString());
            eventObject.Add("MessageTemplate", logEvent.MessageTemplate.ToString());
            eventObject.Add("Exception", logEvent.Exception);
            eventObject.Add("Properties", logEvent.Properties.Dictionary());

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