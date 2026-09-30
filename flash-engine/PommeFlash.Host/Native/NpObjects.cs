using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PommeFlash.Host.Native
{
    /// <summary>
    /// Objets scriptables NPAPI (npruntime) : ceux du module (son objet scriptable) comme ceux de
    /// l'hôte (fenêtre, emplacement, élément de la page), appelés de la même façon, par leur
    /// NPClass. Les objets de l'hôte portent, après l'en-tête NPObject, une poignée vers leur
    /// objet .NET.
    /// </summary>
    static unsafe class NpObjects
    {
        static NPClass* _hostClass;

        [StructLayout(LayoutKind.Sequential)]
        struct HostBlock
        {
            public NPObject header;
            public nint handle;
        }

        public static nint Create(nint npp, NPClass* type)
        {
            if (type == null)
                return 0;
            NPObject* obj = type->allocate != 0
                ? ((delegate* unmanaged[Cdecl]<nint, NPClass*, NPObject*>)type->allocate)(npp, type)
                : (NPObject*)NpMemory.AllocZeroed((nuint)sizeof(NPObject));
            if (obj == null)
                return 0;
            obj->_class = type;
            obj->referenceCount = 1;
            return (nint)obj;
        }

        public static nint Retain(nint pointer)
        {
            if (pointer != 0)
                ((NPObject*)pointer)->referenceCount++;
            return pointer;
        }

        public static void Release(nint pointer)
        {
            if (pointer == 0)
                return;
            var obj = (NPObject*)pointer;
            if (obj->referenceCount == 0 || --obj->referenceCount > 0)
                return;
            if (obj->_class != null && obj->_class->deallocate != 0)
                ((delegate* unmanaged[Cdecl]<NPObject*, void>)obj->_class->deallocate)(obj);
            else
                NpMemory.Free(pointer);
        }

        static NPClass* ClassOf(nint pointer) => pointer == 0 ? null : ((NPObject*)pointer)->_class;

        public static bool HasMethod(nint obj, nint name)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->hasMethod != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, byte>)type->hasMethod)(obj, name) != 0;
        }

        public static bool Invoke(nint obj, nint name, NPVariant* args, uint count, NPVariant* result)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->invoke != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, uint, NPVariant*, byte>)type->invoke)(obj, name, args, count, result) != 0;
        }

        public static bool InvokeDefault(nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->invokeDefault != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, NPVariant*, uint, NPVariant*, byte>)type->invokeDefault)(obj, args, count, result) != 0;
        }

        public static bool HasProperty(nint obj, nint name)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->hasProperty != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, byte>)type->hasProperty)(obj, name) != 0;
        }

        public static bool GetProperty(nint obj, nint name, NPVariant* result)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->getProperty != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, byte>)type->getProperty)(obj, name, result) != 0;
        }

        public static bool SetProperty(nint obj, nint name, NPVariant* value)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->setProperty != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, byte>)type->setProperty)(obj, name, value) != 0;
        }

        public static bool RemoveProperty(nint obj, nint name)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->removeProperty != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, nint, byte>)type->removeProperty)(obj, name) != 0;
        }

        public static bool Enumerate(nint obj, nint* identifiers, uint* count)
        {
            NPClass* type = ClassOf(obj);
            if (type == null || type->structVersion < 2 || type->enumerate == 0)
            {
                *identifiers = 0;
                *count = 0;
                return true;
            }
            return ((delegate* unmanaged[Cdecl]<nint, nint*, uint*, byte>)type->enumerate)(obj, identifiers, count) != 0;
        }

        public static bool Construct(nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            NPClass* type = ClassOf(obj);
            return type != null && type->structVersion >= 2 && type->construct != 0 &&
                   ((delegate* unmanaged[Cdecl]<nint, NPVariant*, uint, NPVariant*, byte>)type->construct)(obj, args, count, result) != 0;
        }

        // ---------------------------------------------------------------
        // Objets de l'hôte
        // ---------------------------------------------------------------

        /// <summary>Nouvel objet NPAPI pour <paramref name="managed"/> (une référence, à libérer).</summary>
        public static NpObjectRef Wrap(HostObject managed)
        {
            var block = (HostBlock*)NpMemory.AllocZeroed((nuint)sizeof(HostBlock));
            block->header._class = HostClass;
            block->header.referenceCount = 1;
            block->handle = GCHandle.ToIntPtr(GCHandle.Alloc(managed));
            return new NpObjectRef((nint)block);
        }

        static NPClass* HostClass
        {
            get
            {
                if (_hostClass != null)
                    return _hostClass;
                var type = (NPClass*)NpMemory.AllocZeroed((nuint)sizeof(NPClass));
                type->structVersion = Np.ClassStructVersion;
                type->deallocate = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HostDeallocate;
                type->invalidate = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HostInvalidate;
                type->hasMethod = (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&HostHasMethod;
                type->invoke = (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, uint, NPVariant*, byte>)&HostInvoke;
                type->invokeDefault = (nint)(delegate* unmanaged[Cdecl]<nint, NPVariant*, uint, NPVariant*, byte>)&HostInvokeDefault;
                type->hasProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&HostHasProperty;
                type->getProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, byte>)&HostGetProperty;
                type->setProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, byte>)&HostSetProperty;
                type->removeProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&HostRemoveProperty;
                _hostClass = type;
                return type;
            }
        }

        static HostObject? Managed(nint obj)
        {
            if (obj == 0 || ((NPObject*)obj)->_class != _hostClass)
                return null;
            nint handle = ((HostBlock*)obj)->handle;
            return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as HostObject;
        }

        static object?[] Arguments(NPVariant* args, uint count)
        {
            var values = new object?[count];
            for (int i = 0; i < count; i++)
                values[i] = NpVariants.Read(args + i);
            return values;
        }

        static byte Guard(Func<bool> action)
        {
            try
            {
                return action() ? (byte)1 : (byte)0;
            }
            catch (Exception ex)
            {
                HostChannel.Error("Objet scriptable : " + ex.Message);
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void HostDeallocate(nint obj)
        {
            var block = (HostBlock*)obj;
            if (block->handle != 0)
                GCHandle.FromIntPtr(block->handle).Free();
            NpMemory.Free(obj);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void HostInvalidate(nint obj)
        {
        }

        /// <summary>Membre de la page que le module demande et que l'hôte ne fournit pas (journal).</summary>
        static byte Traced(nint obj, string kind, string name, byte found)
        {
            if (found == 0)
            {
                string type = Managed(obj)?.GetType().Name ?? "objet";
                HostChannel.Trace(kind + ":" + type + "." + name, $"Page : {kind} {type}.{name} absent de l'hôte.");
            }
            return found;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostHasMethod(nint obj, nint name)
        {
            string method = NpIdentifiers.Describe(name);
            return Traced(obj, "méthode", method, Guard(() => Managed(obj)?.HasMethod(method) == true));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostInvoke(nint obj, nint name, NPVariant* args, uint count, NPVariant* result)
        {
            object?[] values = Arguments(args, count);
            object? value = null;
            string method = NpIdentifiers.Describe(name);
            byte ok = Traced(obj, "appel", method, Guard(() => Managed(obj)?.Invoke(method, values, out value) == true));
            if (ok != 0)
                NpVariants.Write(result, value);
            return ok;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostInvokeDefault(nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            object?[] values = Arguments(args, count);
            object? value = null;
            byte ok = Guard(() => Managed(obj)?.InvokeDefault(values, out value) == true);
            if (ok != 0)
                NpVariants.Write(result, value);
            return ok;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostHasProperty(nint obj, nint name)
        {
            string property = NpIdentifiers.Describe(name);
            return Traced(obj, "propriété", property, Guard(() => Managed(obj)?.HasProperty(property) == true));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostGetProperty(nint obj, nint name, NPVariant* result)
        {
            object? value = null;
            string property = NpIdentifiers.Describe(name);
            byte ok = Traced(obj, "propriété", property, Guard(() => Managed(obj)?.GetProperty(property, out value) == true));
            NpVariants.Write(result, ok != 0 ? value : null);
            return ok;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostSetProperty(nint obj, nint name, NPVariant* value)
        {
            object? managed = NpVariants.Read(value);
            return Guard(() => Managed(obj)?.SetProperty(NpIdentifiers.Describe(name), managed) == true);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HostRemoveProperty(nint obj, nint name)
            => Guard(() => Managed(obj)?.RemoveProperty(NpIdentifiers.Describe(name)) == true);
    }

    /// <summary>Objet de la « page » vu par le module (fenêtre, emplacement, élément…).</summary>
    abstract class HostObject
    {
        public virtual bool HasMethod(string name) => false;

        public virtual bool Invoke(string name, object?[] args, out object? result)
        {
            result = null;
            return false;
        }

        public virtual bool InvokeDefault(object?[] args, out object? result)
        {
            result = null;
            return false;
        }

        public virtual bool HasProperty(string name) => false;

        public virtual bool GetProperty(string name, out object? value)
        {
            value = null;
            return false;
        }

        public virtual bool SetProperty(string name, object? value) => false;

        public virtual bool RemoveProperty(string name) => false;
    }
}
