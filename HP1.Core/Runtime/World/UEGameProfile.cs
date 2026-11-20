// GAME PROFILE — everything Core does differently for HP1 and HP2 (audit/hp2_plan_stage0_1_2026-11-15.md, stage 0).
// The game comes from the install's files (UEEdition.Info.Game: the HP2 Engine.dll build, else HP1); the profile is
// selected once per process by AppInit, next to UEEdition.NativeOrder. A value the port does not know for a game is
// null and refused loudly where it is read — never filled with the HP1 value.
namespace HP1.UEScript
{
    public sealed record UEGameProfile(UEEdition.Game Game, string[] CorePackages,
        (string Class, string Function, UEPortScriptOverrides.Handler Body)[] Overrides,
        UELib.UnrealPackage.GameBuild.BuildName PackageBuild, string NotPorted = null,
        System.Collections.Generic.IReadOnlySet<string> Natives = null)
    {
        // Script packages loaded by an HP1 game run.
        public static readonly UEGameProfile HP1 = new(UEEdition.Game.HP1, new[]
        {
            "Core", "Engine", "HPParticle", "UWindow", "HPBase", "HarryPotter",
            "HPMenu", "HProps", "HPDialog", "Tut1", "Tut3", "Hub2"
        }, UEPortScriptOverrides.HP1Table, UELib.UnrealPackage.GameBuild.BuildName.Unset);   // Unset: UELib's detection, as always

        // HP2 load set (S-6, audit/hp2_plan_stage0_1_2026-11-15.md section 8): the script packages the original has
        // loaded when UGameEngine::Init [HP2 0x103a0fa0] browses its first map — Core (appInit) and Engine (Game.exe
        // InitEngine 0x1090fa51: StaticLoadClass "ini:Engine.Engine.GameEngine"). WinDrv, Render and ALAudio are
        // DLL-only packages in HP2 (no .u ships); every other script package arrives through the maps' import tables
        // (UEActorGraphLoader.LoadImportedScriptPackages), as the linker loads it.
        // Overrides: UEPortScriptOverrides.HP2Table (author decisions 2026-11-16, NOT ORIGINAL): the options page's Video
        // column and HP1's improvements where hgame.u has the same defect.
        // NotPorted: why a player frontend cannot run the game yet (the headless Boot runs it to its first STOP).
        // PackageBuild HP2: the v79 token map of the UELib fork (HP1.Core/ThirdParty/README.md), never auto-detected.
        // Natives: the natives whose HP2 body is matched (stage 4), one row each with its evidence in audit/hp2_natives.tsv
        // (checked by audit/hp2_native_judge.sh); every other native call is a loud STOP (UENativesCore.Pair registers
        // only these).
        public static readonly UEGameProfile HP2 = new(UEEdition.Game.HP2, new[] { "Core", "Engine" },
            UEPortScriptOverrides.HP2Table,
            UELib.UnrealPackage.GameBuild.BuildName.HP2,
            "Harry Potter and the Chamber of Secrets (HP2) is recognised but not playable: the headless boot runs to its "
            + "first unported native; natives (stage 4), physics (5), render (6), audio (7) and saves (8) are not ported "
            + "(audit/hp2_plan_stage0_1_2026-11-15.md)",
            new System.Collections.Generic.HashSet<string>
            {
                "Abs", "Add_ColorColor", "Add_FloatFloat", "Add_IntInt", "Add_RotatorRotator", "Add_VectorVector",
                "AddAdd_Byte", "AddAdd_Int", "AddAdd_PreByte", "AddAdd_PreInt", "AddEqual_ByteByte",
                "AddEqual_FloatFloat", "AddEqual_IntInt", "AddEqual_RotatorRotator", "AddEqual_VectorVector", "AddParticle",
                "AddPawn", "AllActors", "AutonomousPhysics", "actorReachable", "And_IntInt", "AndAnd_BoolBool", "Asc", "At_StrStr", "Atan", "AttachDecal", "BasedActors",
                "BoneName", "BoneNumber", "BonePos", "BoneRot", "CanSee", "Caps", "ChildActors", "Chr", "Clamp", "ClassIsChildOf",
                "ClearPaths", "ClientHearSound", "ClientTravel", "CompareGesture", "CompareGesturePoint", "Complement_PreInt", "ComplementEqual_FloatFloat",
                "ComplementEqual_StrStr", "Concat_StrStr", "ConsoleCommand", "Cos", "CreateAnimChannel",
                "CreateNativeFont",
                "CreateTextureFromBMP", "CreateTextureFromScreenShot", "Cross_VectorVector", "DemoPlaySound", "describeSpec",
                "Destroy", "DetachDecal", "Disable", "Divide_FloatFloat", "Divide_IntInt", "Divide_RotatorFloat",
                "Divide_VectorFloat", "DivideEqual_ByteByte", "DivideEqual_FloatFloat", "DivideEqual_IntFloat",
                "DivideEqual_RotatorFloat", "DivideEqual_VectorFloat", "Dot_VectorVector", "DrawActor", "DrawClippedActor",
                "DrawText", "DrawTextClipped", "DrawTile", "DrawTileClipped", "DynamicLoadObject", "EAdjustJump", "Enable", "EqualEqual_BoolBool",
                "EqualEqual_FloatFloat", "EqualEqual_IntInt", "EqualEqual_NameName", "EqualEqual_ObjectObject",
                "EqualEqual_RotatorRotator", "EqualEqual_StrStr", "EqualEqual_VectorVector", "Error", "Exp", "FastTrace",
                "FClamp", "FindBestInventoryPath", "FindPath", "FindPathTo", "FindPathToward", "FindRandomDest",
                "FindStairRotation", "FinishAnim", "FinishInterpolation", "FMax", "FMin", "FRand", "GetAnimGroup", "GetAxes", "GetCurrentKeyState",
                "GetAddressURL", "GetDefaultURL", "GetEntryLevel", "GetEnum", "GetLanguage", "GetLocalURL", "GetMapName", "GetNextInt",
                "GetNetworkNumber", "GetNextIntDesc", "GetNextSkin", "GetParticleParams", "GetPlayerNetworkAddress", "GetPropertyText", "GetRenderExtent", "GetSoundDuration", "GetStateName", "GetUnAxes", "GetURLMap", "GetWind", "GetWorldCollisionBox",
                "GotoState", "Greater_FloatFloat", "Greater_IntInt", "Greater_StrStr", "GreaterEqual_FloatFloat",
                "GreaterEqual_IntInt", "GreaterEqual_StrStr", "GreaterGreater_IntInt", "GreaterGreater_VectorRotator",
                "GreaterGreaterGreater_IntInt", "HasAnim", "InStr", "Invert", "IsA", "IsAnimating", "IsInState",
                "IsOSVer2kOrXP", "IsSoftwareRendering", "Left", "Len", "Lerp", "Less_FloatFloat", "Less_IntInt",
                "Less_StrStr", "LessEqual_FloatFloat", "LessEqual_IntInt", "LessEqual_StrStr", "LessLess_IntInt",
                "LessLess_VectorRotator", "LineOfSightTo", "LinkSkelAnim", "LoadGameSaveInfo", "Localize", "LoopAnim", "Log", "Loge", "MakeNoise", "Move",
                "Max", "Mid", "Min", "MirrorVectorByNormal", "ModifySound", "MoveSmooth", "MoveTo", "MoveToward", "Multiply_ColorFloat", "Multiply_FloatColor",
                "Multiply_FloatFloat", "Multiply_FloatRotator", "Multiply_FloatVector", "Multiply_IntInt",
                "Multiply_RotatorFloat", "Multiply_VectorFloat", "Multiply_VectorVector", "MultiplyEqual_ByteByte",
                "MultiplyEqual_FloatFloat", "MultiplyEqual_IntFloat", "MultiplyEqual_RotatorFloat",
                "MultiplyEqual_VectorFloat", "MultiplyEqual_VectorVector", "MultiplyMultiply_FloatFloat", "Normal",
                "Normalize", "Not_PreBool", "NotEqual_BoolBool", "NotEqual_FloatFloat", "NotEqual_IntInt",
                "NotEqual_NameName", "NotEqual_ObjectObject", "NotEqual_RotatorRotator", "NotEqual_StrStr",
                "NotEqual_VectorVector", "NumParticles", "Or_IntInt", "OrOr_BoolBool", "OrthoRotation", "ParseKillMessage",
                "Percent_FloatFloat", "PickAnyTarget", "PickTarget", "PlayAnim", "PlayerCanSeeMe", "PlayMusic", "PlayOwnedSound", "PlaySound", "pointReachable", "RadiusActors", "Rand",
                "RandRange", "RecomputeDeltas", "RemovePawn", "ResetConfig", "ResetKeyboard", "Right", "RotRand", "SaveConfig", "SaveGameExists", "SaveGameSaveInfo",
                "ScreenToWorld", "ScriptedTexture.DrawColoredText", "ScriptedTexture.DrawText", "ScriptedTexture.TextSize", "SetBase", "SetCollision", "SetCollisionSize", "SetLocation", "SetOwner", "SetParticleParams", "SetPhysics", "SetPropertyText", "SetRotation",
                "SetTimer", "Sin", "Sleep", "Smerp", "Spawn", "Sqrt", "Square", "StaticSaveConfig", "StopAllMusic", "StopMusic", "StopSound", "StopWaiting",
                "StrafeFacing", "StrafeTo", "StrLen", "Subtract_ColorColor", "Subtract_FloatFloat", "Subtract_IntInt", "Subtract_PreFloat",
                "Subtract_PreInt", "Subtract_PreVector", "Subtract_RotatorRotator", "Subtract_VectorVector",
                "SubtractEqual_ByteByte", "SubtractEqual_FloatFloat", "SubtractEqual_IntInt",
                "SubtractEqual_RotatorRotator", "SubtractEqual_VectorVector", "SubtractSubtract_Byte",
                "SubtractSubtract_Int", "SubtractSubtract_PreByte", "SubtractSubtract_PreInt", "Tan", "TextSize", "TouchingActors",
                "Trace", "TraceActors", "TraceTexture", "TurnTo", "TurnToward", "TweenAnim", "UnWarp", "UpdateURL", "VisibleActors", "VisibleCollidingActors", "VRand", "VSize",
                "WaitForLanding", "Warn", "Warp", "Xor_IntInt", "XorXor_BoolBool", "ZoneActors"
            });

        public static UEGameProfile Of(UEEdition.Game game) => game == UEEdition.Game.HP2 ? HP2 : HP1;

        // The profile of this process's install (set by UELevelRuntime.AppInit; HP1 until then, as before HP2 existed).
        public static UEGameProfile Current { get; internal set; } = HP1;

        // The value or a loud refusal naming what is not ported for this game.
        public static T Known<T>(T value, string what) where T : class
            => value ?? throw new System.NotSupportedException(
                $"{Current.Game}: {what} is not ported for this game (audit/hp2_plan_stage0_1_2026-11-15.md)");

        // A seam whose body differs in that game and is not ported for it: a loud stop naming it, never the other
        // game's code.
        public static void Stop(UEEdition.Game game, string what)
        {
            if (Current.Game == game)
                throw new System.NotSupportedException(
                    $"{game}: {what} is not ported for this game (audit/hp2_plan_stage0_1_2026-11-15.md)");
        }
    }
}
