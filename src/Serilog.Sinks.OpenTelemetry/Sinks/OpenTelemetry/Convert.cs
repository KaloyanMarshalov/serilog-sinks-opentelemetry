// Copyright 2022 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Trace;
using Serilog.Events;

namespace Serilog.Sinks.OpenTelemetry;

internal static class Convert
{
    internal static string SCHEMA_URL = "https://opentelemetry.io/schemas/v1.13.0";

    internal static RepeatedField<KeyValue> ToResourceAttributes(IDictionary<string, Object>? resourceAttributes)
    {
        var attributes = new RepeatedField<KeyValue>();
        if (resourceAttributes != null)
        {
            foreach (KeyValuePair<string, Object> entry in resourceAttributes)
            {
                var v = ConvertUtils.ToOpenTelemetryPrimitive(entry.Value);
                if (v != null)
                {
                    var kv = new KeyValue();
                    kv.Value = v;
                    kv.Key = entry.Key;
                    attributes.Add(kv);
                }
            }
        }
        return attributes;
    }

    internal static LogRecord ToLogRecord(LogEvent logEvent, string? renderedMessage)
    {
        var logRecord = new LogRecord();
        ProcessTimestamps(logRecord, logEvent);
        ProcessSeverity(logRecord, logEvent);
        ProcessBody(logRecord, logEvent, renderedMessage);
        // Perhaps a bulk "ProcessAttributes" instead?
        ProcessException(logRecord, logEvent);

        return logRecord;
    }
    
    internal static void ProcessTimestamps(LogRecord logRecord, LogEvent logEvent)
    {
        logRecord.TimeUnixNano = ConvertUtils.ToUnixNano(logEvent.Timestamp);
        // Set the ObservedTimestamp as this is technically already part of the OTEL pipeline
        // We can have a discussion on leaving that to the collector instead?
        logRecord.ObservedTimeUnixNano = ConvertUtils.ToUnixNano(DateTimeOffset.Now);
    }
    
    internal static void ProcessSeverity(LogRecord logRecord, LogEvent logEvent)
    {
        var level = logEvent.Level;
        logRecord.SeverityText = level.ToString();
        logRecord.SeverityNumber = ConvertUtils.ToSeverityNumber(level);
    }

    // According to the OTEL Data model, Body is of type "any", however, for Elasticsearch indexing purposes we need to 
    // be more specific and define it as a map <string, any> instead.
    // The Body field has 2 fields according to our spec - "message" and "properties"
    // "message" is a string - the log message that was written by the caller
    // "properties" is a map<string, any> - used for structured logging to populate additional fields
    internal static void ProcessBody(LogRecord logRecord, LogEvent logEvent, string? renderedMessage)
    {
        var logBody = new KeyValueList();
        if (renderedMessage != null && renderedMessage.Trim() != "")
        {
            logBody.Values.Add(new KeyValue()
            {
                // TODO: Make this a constant somewhere reasonable
                Key = "message",
                Value = new AnyValue{StringValue = renderedMessage}
            });
        }

        var logBodyProperties = new KeyValueList();
        foreach (var property in logEvent.Properties)
        {
            // TraceId and SpanId are not separated out in the Serilog LogEvent, they're bundled in with other properties.
            // We need to check for them and if we find them, write them in the OpenTelemetry LogRecord in the corresponding field
            switch (property.Key)
            {
                case TraceIdEnricher.TRACE_ID_PROPERTY_NAME:
                    var traceId = ConvertUtils.ToOpenTelemetryTraceId(property.Value.ToString());
                    if (traceId != null)
                    {
                        logRecord.TraceId = traceId;
                    }
                    break;

                case TraceIdEnricher.SPAN_ID_PROPERTY_NAME:
                    var spanId = ConvertUtils.ToOpenTelemetrySpanId(property.Value.ToString());
                    if (spanId != null)
                    {
                        logRecord.SpanId = spanId;
                    }
                    break;

                default:
                    var value = ConvertUtils.ToOpenTelemetryAnyValue(property.Value);
                    if (value != null)
                    {
                        logBodyProperties.Values.Add(new KeyValue()
                        {
                            Key = property.Key,
                            Value = value
                        });
                    }
                    break;
            }
        }

        logBody.Values.Add(new KeyValue()
        {
            // TODO: Make this a constant somewhere reasonable
            Key = "properties",
            Value = new AnyValue{KvlistValue = logBodyProperties}
        });
        
        logRecord.Body = new AnyValue()
        {
            KvlistValue = logBody
        };
    }

    internal static void ProcessException(LogRecord logRecord, LogEvent logEvent)
    {
        var ex = logEvent.Exception;
        if (ex != null)
        {
            var attrs = logRecord.Attributes;

            attrs.Add(ConvertUtils.NewStringAttribute(TraceSemanticConventions.AttributeExceptionType, ex.GetType().ToString()));

            if (ex.Message != "")
            {
                attrs.Add(ConvertUtils.NewStringAttribute(TraceSemanticConventions.AttributeExceptionMessage, ex.Message));
            }

            if (ex.ToString() != "")
            {
                attrs.Add(ConvertUtils.NewStringAttribute(TraceSemanticConventions.AttributeExceptionStacktrace, ex.ToString()));
            }
        }
    }
}
