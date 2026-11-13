namespace HP1.UEScript
{
    // ── AActor::IsBlockedBy (Engine.dll.c:21459-21545) ──────────────────────
    //
    // THE single blocking authority. Every collision consumer — ULevel::MoveActor,
    // ULevel::SingleLineCheck, SpawnActor's mutual-block test and
    // CheckEncroachment — asks this and nothing else. It lives in Core because
    // no two frontends may disagree about who blocks whom.
    //
    // The shipped body picks the block flag SEPARATELY FOR EACH SIDE:
    //
    //     blocked = (self  playerish ? other.bBlockPlayers : other.bBlockActors)
    //            && (other playerish ? self.bBlockPlayers  : self.bBlockActors)
    //
    // It is NOT "if either side is playerish, compare bBlockPlayers on both".
    // The two reads are at 103522ea/103522f5 (first side, >>2 vs >>3 of the
    // other's 0x1dc) and 10352369/its fallthrough (second side, same shift pair
    // on self). 0x1dc: bit1=bCollideWorld, bit2=bBlockActors, bit3=bBlockPlayers.
    public static class UEBlocking
    {
        // "Playerish" for the ORDINARY (neither side a brush) branch: a
        // PlayerPawn counts only with a non-NULL Player (0x4bc — an unpossessed
        // one stays on bBlockActors), or any Projectile (103522f5 walks the
        // class chain for AProjectile after the PlayerPawn test).
        public static bool UsesPlayerBlocking(UEObject actor)
            => actor is not null
               && ((UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && actor.GetProp(PK.Player) is not null)
                   || UEInterpreter.ClassIsA(actor.Class, "Projectile"));

        // "Playerish" for the BRUSH branches — a shipped ASYMMETRY, not an
        // oversight of ours: joined_r0x103521a0 and joined_r0x1035223b walk the
        // class chain for APlayerPawn ONLY and never test AProjectile. So a
        // projectile against a brush is compared on bBlockActors, while the
        // same projectile against a cylinder actor is compared on bBlockPlayers.
        static bool UsesPlayerBlockingVsBrush(UEObject actor)
            => actor is not null
               && UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
               && actor.GetProp(PK.Player) is not null;

        // A brush side per shipped: a non-NULL Brush model AND the ABrush class.
        // Public because it is the SINGLE definition of "is a brush" for every
        // collision path — IsBlockedBy, CheckEncroachment's test and push
        // condition, and the encroachment candidate walk all read the same two
        // shipped tests (`*(int *)(x + 0x168) != 0` plus the ABrush class walk).
        public static bool IsBrush(UEObject actor)
            => actor.GetProp(PK.Brush) is not null
               && UEInterpreter.ClassIsA(actor.Class, "Brush");

        static bool Flag(UEObject actor, bool playerish)
            => UEInterpreter.ToBool(actor.GetProp(playerish ? "bBlockPlayers" : "bBlockActors"));

        // `levelInfo` is the world sentinel (shipped compares against
        // this->Level at 0x7c). Pass the interpreter's LevelInfo; null simply
        // means "no world actor in play".
        public static bool IsBlockedBy(UEObject actor, UEObject other, UEObject levelInfo)
        {
            if (actor is null || other is null) return false;

            // Level (the world) blocks purely on the mover's bCollideWorld
            // (21460-21462: other == this->Level → return bit1 of 0x1dc).
            UEObject actorLevel = actor.GetProp(PK.Level) as UEObject ?? levelInfo;
            if (ReferenceEquals(other, actorLevel))
                return UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld));

            // Shipped tests BOTH conditions on each side (21462 / 21475:
            // `*(int *)(x + 0x168) != 0` AND a class-chain walk for ABrush).
            // A non-Brush class that happens to carry a Brush model must stay
            // on the ORDINARY rule, not the brush rule — the two branches read
            // different flags, so collapsing this to the Brush pointer alone
            // silently rewrites the blocking formula for such an actor.
            bool otherBrush = IsBrush(other);
            bool selfBrush = IsBrush(actor);

            // Other is a brush (21462-21474): demand the CYLINDER side's
            // bCollideWorld, then the brush's own block flag chosen by whether
            // the cylinder side is a possessed PlayerPawn.
            if (otherBrush)
            {
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))) return false;
                return Flag(other, UsesPlayerBlockingVsBrush(actor));
            }

            // Self is a brush (21475-21486): the mirrored test.
            if (selfBrush)
            {
                if (!UEInterpreter.ToBool(other.GetProp(PK.bCollideWorld))) return false;
                return Flag(actor, UsesPlayerBlockingVsBrush(other));
            }

            // Ordinary pair (21487-21545): each side picks its own flag.
            if (!Flag(other, UsesPlayerBlocking(actor))) return false;
            return Flag(actor, UsesPlayerBlocking(other));
        }
    }
}
