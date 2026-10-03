namespace Sts2Sim.Core.Random;

/// <summary>Optional label-only failure notification; ordinary shuffle scopes are unchanged.</summary>
public interface IAbortableLabelShuffleBoundary : IDisposable
{
    void Abort(Exception nativeError);
}
