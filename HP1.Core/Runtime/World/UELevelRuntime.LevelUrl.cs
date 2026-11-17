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

        // Original: Engine.dll ?execGetAddressURL@ALevelInfo@@QAEXAAUFFrame@@QAX@Z [HP1 0x10407d20] [HP2 0x10415470]
        // P_FINISH; *(FString*)Result = FString::Printf("%s:%i", *XLevel->URL.Host, XLevel->URL.Port) (ULevel URL +0x44:
        // Host +0x50, Port +0x5c). The level URL's Host is LastUrl's (AddOption/UpdateURL never touch it); Port is
        // FURL::DefaultPort for every URL the port holds (UEUrl).
        public string AddressUrlNative()
        {
            if (LastUrl is null)
                throw new System.InvalidOperationException("GetAddressURL: level URL not set (level not loaded)");
            return LastUrl.Host + ":" + UEUrl.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // AActor::execGetURLMap (Engine.dll.c:99279, native 547): copies XLevel(+0x80)->Engine(+0x40) +0x98 =
        // UGameEngine LastURL (+0x7c, ctor ??0UGameEngine) .Map (FURL +0x1c, FURL::FURL Engine.dll.c:109991).
        public string UrlMapNative()
        {
            if (LastUrl is null)
                throw new System.InvalidOperationException("GetURLMap: LastURL not set (level not loaded)");
            return LastUrl.Map;
        }
    }
}
