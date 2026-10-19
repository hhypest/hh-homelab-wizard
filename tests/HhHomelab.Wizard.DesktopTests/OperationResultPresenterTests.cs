using HhHomelab.Wizard.Desktop.Services;
using HhHomelab.Wizard.Domain;
using Xunit;

namespace HhHomelab.Wizard.DesktopTests;

public sealed class OperationResultPresenterTests
{
    [Fact]
    public void EverySafeCodeHasPresentationAndUnknownNeverSuggestsAWrite()
    {
        foreach (var code in Enum.GetValues<OperationErrorCode>().Where(code => code != OperationErrorCode.None))
        {
            var result = OperationResult.Failed(OperationOutcomeKind.Unknown, code);
            var presentation = OperationResultPresenter.Describe(result);
            Assert.False(string.IsNullOrWhiteSpace(presentation.Message));
            Assert.Equal(code == OperationErrorCode.JournalUnavailable ? OperationUserAction.CheckJournal : OperationUserAction.Observe, presentation.Action);
            Assert.DoesNotContain("отклон", presentation.Message);
            Assert.DoesNotContain("до отправки", presentation.Message);
        }
    }

    [Theory]
    [InlineData(OperationOutcomeKind.Invalid, OperationErrorCode.AuthenticationRequired, OperationUserAction.SignIn)]
    [InlineData(OperationOutcomeKind.Invalid, OperationErrorCode.VersionUnsupported, OperationUserAction.CheckCompatibility)]
    [InlineData(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed, OperationUserAction.CheckConnection)]
    [InlineData(OperationOutcomeKind.Transport, OperationErrorCode.DeadlineExceeded, OperationUserAction.CheckConnection)]
    [InlineData(OperationOutcomeKind.Conflict, OperationErrorCode.ObservationChanged, OperationUserAction.ReviewPlan)]
    [InlineData(OperationOutcomeKind.Rejected, OperationErrorCode.WriteRejected, OperationUserAction.ReviewPlan)]
    [InlineData(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled, OperationUserAction.None)]
    public void TypedCodeSelectsActionWithoutParsingAnException(OperationOutcomeKind kind, OperationErrorCode code, OperationUserAction action)
    {
        Assert.Equal(action, OperationResultPresenter.Describe(OperationResult.Failed(kind, code)).Action);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifiedSuccessDoesNotOfferAnotherApply(bool applied)
    {
        var presentation = OperationResultPresenter.Describe(OperationResult.Completed(applied));
        Assert.Equal(OperationUserAction.None, presentation.Action);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Message));
    }
}
