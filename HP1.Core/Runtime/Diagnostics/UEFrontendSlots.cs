// Inventory of Core DELEGATE slots occupied by code OUTSIDE Core.
//
// WHY. A source scan only sees `PhysicsHook =` as text; it cannot tell what actually sits in the live runtime's
// slots, who filled them, or whether a new one appeared, and it misses assignment through an alias, from another
// directory, from editor code or via reflection.
//
// PROVENANCE IS BY IDENTITY, NOT BY VALUE. "Slot not null" does NOT mean "the frontend filled it": Core fills some
// slots itself (UELevelRuntime.Guard and UELifecycle.Guard default to "run the body"), and "not null => frontend"
// would be proof by correlation. So we check WHICH ASSEMBLY DECLARES the delegate's method: a body declared
// outside the Core assembly has a foreign author, whatever it is (a frontend lambda compiles into its own
// closure type).
//
// SCOPE LIMIT, stated explicitly: only DELEGATE slots are listed. Non-delegate injection points
// (Interp.Natives, Runtime.FrameProducer, Runtime.MeshCatalog) are NOT covered by this inventory; the frontends
// supply them, and replacing them is a separate question. Silence of the inventory is not proof they are absent.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace HP1.UEScript
{
    public static class UEFrontendSlots
    {
        // The Core assembly. Anything declared NOT here has a foreign author.
        static Assembly CoreAssembly => typeof(UELevelRuntime).Assembly;

        // Exact SET of slots occupied by foreign code, names of the form "Type.Member".
        // SortedSet keeps the report stable and the set comparison honest.
        public static SortedSet<string> Occupied(UELevelRuntime rt)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            if (rt is null) return found;
            Scan(rt, typeof(UELevelRuntime), nameof(UELevelRuntime), found);
            Scan(null, typeof(UEInterpreter), nameof(UEInterpreter), found);
            Scan(null, typeof(UELifecycle), nameof(UELifecycle), found);
            Scan(rt.Input, typeof(UEInput), nameof(UEInput), found);
            if (rt.FrameProducer is not null)
                Scan(rt.FrameProducer, typeof(UEWorldFrameProducer),
                     nameof(UEWorldFrameProducer), found);
            return found;
        }

        static void Scan(object owner, Type type, string label, SortedSet<string> found)
        {
            BindingFlags flags = BindingFlags.Public
                | (owner is null ? BindingFlags.Static : BindingFlags.Instance);
            foreach (FieldInfo f in type.GetFields(flags))
            {
                if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                Record(label + "." + f.Name, Read(() => f.GetValue(owner)), found);
            }
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                if (!typeof(Delegate).IsAssignableFrom(p.PropertyType)) continue;
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                Record(label + "." + p.Name, Read(() => p.GetValue(owner)), found);
            }
        }

        // An unreadable slot is NOT "slot free". It enters the inventory loudly under a separate name
        // instead of disappearing silently.
        static object Read(Func<object> get)
        {
            try { return get(); }
            catch (Exception e) { return e; }
        }

        static void Record(string name, object value, SortedSet<string> found)
        {
            if (value is Exception) { found.Add(name + " (UNREADABLE)"); return; }
            if (!(value is Delegate d)) return;
            foreach (Delegate part in d.GetInvocationList())
            {
                Type declaring = part.Method?.DeclaringType;
                // DeclaringType == null happens for a dynamic method: the author cannot be established, so it
                // counts as FOREIGN. Deliberate direction: an error this way adds a manifest line and fails
                // the check; the other way it would silently hide the owner.
                if (declaring is null || declaring.Assembly != CoreAssembly)
                {
                    found.Add(name);
                    return;
                }
            }
        }
    }
}
