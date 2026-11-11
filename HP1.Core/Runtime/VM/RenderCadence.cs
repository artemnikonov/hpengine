namespace HP1.UEScript
{
    // 60 Hz CADENCE FOR FRAME-DEPENDENT RENDER STEPS (D-35, author decision: fix original's problems). Shipped steps
    // procedural and animated textures in Lock/Update of the drawn frame (UTexture::Tick with MaxFrameRate=0 is a
    // step without time), and the player saw it at 60 Hz. The port draws at the monitor rate (60/120/144/240 Hz,
    // any OS), so steps are metered by wall clock: 60 per second. A quarter-period tolerance absorbs frame jitter
    // at 60 Hz; a lag longer than one period is not caught up with a queue of steps (one skipped batch, no jerk).
    public struct RenderCadence
    {
        public const double Period = 1.0 / 60.0;
        double _next;

        public bool Due(double nowSeconds)
        {
            if (nowSeconds < _next - Period / 4) return false;
            _next = nowSeconds - _next > Period ? nowSeconds + Period : _next + Period;
            return true;
        }
    }
}
