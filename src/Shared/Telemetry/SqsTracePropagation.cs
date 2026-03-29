using System.Diagnostics;
using Amazon.SQS.Model;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Shared.Telemetry;

public static class SqsTracePropagation
{
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    public static void Inject(IDictionary<string, MessageAttributeValue> attributes)
    {
        Propagator.Inject(
            new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current),
            attributes,
            (attrs, key, value) =>
            {
                attrs[key] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = value
                };
            });
    }

    public static PropagationContext Extract(IDictionary<string, MessageAttributeValue> attributes)
    {
        return Propagator.Extract(
            default,
            attributes,
            (attrs, key) =>
            {
                if (attrs.TryGetValue(key, out var attr) && attr.DataType == "String")
                    return [attr.StringValue];
                return [];
            });
    }
}
