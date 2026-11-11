// ALevelInfo::execGetLocalURL (Engine.dll.c:68560): FURL::String(XLevel->URL),
// the level URL is ULevel+0x44. UGameEngine::LoadMap writes it with the same URL as
// LastURL (successful load); the port keeps that URL in LastUrl (AdoptLoadedUrl).
// The string is the local form of FURL::String: Map, "?"+options, "#"+Portal
// (UEUrl.String). Campaign consumers: HPConsole.RenderUWindow
// (`InStr(Caps(url), "STARTUP")`, HPConsole.uc:751) and Log (:749).

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public string LocalUrlNative()
        {
            if (LastUrl is null)
                throw new System.InvalidOperationException("GetLocalURL: level URL not set (level not loaded)");
            return LastUrl.String();
        }
    }
}
