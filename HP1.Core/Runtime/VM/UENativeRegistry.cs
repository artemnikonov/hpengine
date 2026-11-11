// The native-function dispatch seam between the Core interpreter and a
// frontend's native table (UENatives in the Unity tree, HeadlessNatives in
// the headless smoke). The interpreter only ever resolves handlers and
// reports unknowns; registration/ownership stays with the frontend.
namespace HP1.UEScript
{
    public delegate object UENativeHandler(UEInterpreter interp, UEFrame frame, object[] args);

    public interface IUENativeRegistry
    {
        UENativeHandler GetByIndex(int idx);
        UENativeHandler GetByName(string name);
        // Loud unknown-native report (§8) — headless THROWS, Unity logs.
        void OnUnknownNative(int idx, string name, string context);
        // Human name of a registered index (coverage diagnostics), or null.
        string NameOfIndex(int idx);
    }
}
