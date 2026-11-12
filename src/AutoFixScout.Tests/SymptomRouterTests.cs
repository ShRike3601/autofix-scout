using AutoFixScout.Areas;
using Xunit;

namespace AutoFixScout.Tests;

public class SymptomRouterTests
{
    [Theory]
    [InlineData("there is no sound from my laptop speakers", "sound")]
    [InlineData("people on Zoom say they can't hear me", "microphone")]
    [InlineData("my mic is not working in teams", "microphone")]
    [InlineData("webcam shows a black picture", "camera")]
    [InlineData("wifi connected but websites are not loading", "network")]
    [InlineData("Chrome keeps crashing every few minutes", "program")]
    [InlineData("the game won't open, says VCRUNTIME140.dll is missing", "program")]
    [InlineData("my laptop is very slow and the disk is full", "storage")]
    [InlineData("windows update failed with error 0x80070005", "update")]
    [InlineData("printer is offline and won't print", "printer")]
    [InlineData("second monitor not showing anything", "display")]
    [InlineData("I keep getting a blue screen", "health")]
    [InlineData("the start menu does not open", "health")]
    [InlineData("something is wrong", "health")]
    [InlineData("", "health")]
    public void RoutesToTheRightCheck(string text, string area) => Assert.Equal(area, SymptomRouter.Best(text).Area);

    [Fact]
    public void Explanations_FollowTheWritingGuide()
    {
        foreach (var r in SymptomRouter.Routes.Values)
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", r.Explanation);
    }
}
