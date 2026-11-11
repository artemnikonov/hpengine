// Reference render pass: translation of Render.dll over the world mirror (assembly HP1.Reference, validation only;
// not shipped in the game). The production Core frame does not build it
// (UEWorldFrameProducer.UseTranslatedRenderPass = false). Through this seam the reference frame mode (fixtures,
// comparisons against the translation) gets the pass without coupling Core to the translation.
using System.Collections.Generic;

namespace HP1.UEScript
{
    public interface ITranslatedRenderPass
    {
        void BeginFrame(UEObject player, UEVector location, UERotator rotation, int sizeX, int sizeY, float viewFov);
        void DrawWorld(UEObject player);
        IReadOnlyList<UEObject> DrawnParticles { get; }
        void EndFrame();
    }
}
