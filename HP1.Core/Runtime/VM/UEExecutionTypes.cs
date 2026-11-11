namespace HP1.UEScript
{
    // Why the state frame yielded: the port's model of FStateFrame.LatentAction (+0x28; in shipped the latent
    // native's number, e.g. 0x180 = Sleep, AActor::execSleep, Engine.dll.c:124852). Polled by the
    // AActor::ProcessState loop (Engine.dll.c:45801): it steps only while LatentAction == 0.
    public enum LatentRunState
    {
        Continue,
        Stop,
        Sleep,
        FinishAnim,
        FinishInterpolation,
        MoveTo,
        MoveToward,
        StrafeTo,
        StrafeFacing,
        TurnTo,
        TurnToward,
        WaitForLanding,
    }

    // Internal statement outcome code in the port's Run. Shipped has no separate structure: ProcessInternal
    // (Core.dll.c:21624) steps until opcode 0x04, iterators wait for 0x30/0x31, execSwitch moves Code itself.
    public enum UEStmtResult
    {
        Next,
        Return,
        Stop,
        Jump,
        Switch,
        GotoLabel,
        Iterator,
        IteratorNext,
        IteratorPop,
    }

    public struct UEEvalResult
    {
        public object Value;
        public UEStmtResult Result;
        public int JumpAddress;
        public string Label;
    }
}
