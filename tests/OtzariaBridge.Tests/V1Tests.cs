using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>בדיקות v1-pipefix: אימות שרת ה-pipe מול pid של ה-SCM (בלי תלות בסשן/נתיב תמונה)</summary>
public class V1Tests
{
    [Fact]
    public void Pipe_pid_must_equal_scm_service_pid()
    {
        Assert.True(PipePeer.IsServicePid(1234, 1234));
        Assert.False(PipePeer.IsServicePid(1234, 4321));   // pipe מזויף של תהליך משתמש
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1234, 0)]    // השירות לא רץ / אין גישה ל-SCM: לא נפתח
    [InlineData(0, 1234)]
    [InlineData(-1, -1)]
    public void Unknown_pids_fail_closed(long pipePid, long scmPid) =>
        Assert.False(PipePeer.IsServicePid(pipePid, scmPid));

    [Fact]
    public void Scm_query_for_missing_service_returns_zero() =>
        Assert.Equal(0u, PipePeer.ServicePidFromScm("OtzariaNoSuchService_xyz"));
}
