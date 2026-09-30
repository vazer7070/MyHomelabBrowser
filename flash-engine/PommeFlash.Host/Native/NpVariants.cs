namespace PommeFlash.Host.Native
{
    /// <summary>Valeurs échangées avec le module (NPVariant) : conversion vers et depuis .NET.</summary>
    static unsafe class NpVariants
    {
        /// <summary>« null » JavaScript, distinct de « undefined » (null en .NET).</summary>
        public static readonly object JsNull = new();

        public static void Write(NPVariant* target, object? value)
        {
            *target = default;
            switch (value)
            {
                case null:
                    target->type = NPVariantType.Void;
                    break;
                case bool flag:
                    target->type = NPVariantType.Bool;
                    target->boolValue = flag ? (byte)1 : (byte)0;
                    break;
                case int number:
                    target->type = NPVariantType.Int32;
                    target->intValue = number;
                    break;
                case double number:
                    target->type = NPVariantType.Double;
                    target->doubleValue = number;
                    break;
                case string text:
                    target->type = NPVariantType.String;
                    target->stringValue.UTF8Characters = NpMemory.Utf8(text, out uint length);
                    target->stringValue.UTF8Length = length;
                    break;
                case NpObjectRef reference:
                    target->type = NPVariantType.Object;
                    target->objectValue = reference.Pointer;
                    NpObjects.Retain(reference.Pointer);
                    break;
                default:
                    target->type = value == JsNull ? NPVariantType.Null : NPVariantType.Void;
                    break;
            }
        }

        public static object? Read(NPVariant* source) => source->type switch
        {
            NPVariantType.Null => JsNull,
            NPVariantType.Bool => source->boolValue != 0,
            NPVariantType.Int32 => source->intValue,
            NPVariantType.Double => source->doubleValue,
            NPVariantType.String => NpMemory.ReadUtf8(source->stringValue.UTF8Characters, source->stringValue.UTF8Length),
            NPVariantType.Object => new NpObjectRef(source->objectValue),
            _ => null
        };

        public static void Release(NPVariant* variant)
        {
            if (variant == null)
                return;
            if (variant->type == NPVariantType.String)
                NpMemory.Free(variant->stringValue.UTF8Characters);
            else if (variant->type == NPVariantType.Object)
                NpObjects.Release(variant->objectValue);
            *variant = default;
        }
    }

    /// <summary>Objet NPAPI désigné par son adresse (celle du module ou de l'hôte).</summary>
    readonly record struct NpObjectRef(nint Pointer);
}
