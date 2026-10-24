using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;

namespace AiTools.Extensions.Scripting;

internal static class JintObjectExtensions
{
    public static void FastAddProperty(this ObjectInstance instance, string name, object? value, bool writable, bool enumerable, bool configurable)
    {
        var jsValue = value as JsValue ?? JsValue.FromObject(instance.Engine, value);
        instance.FastSetProperty(name, new PropertyDescriptor(jsValue, writable, enumerable, configurable));
    }
}
