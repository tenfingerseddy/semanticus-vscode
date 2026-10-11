// The Plan page's "What your assistant sees" panel, held to the engine's own fold line (lane U1, contract 6.1).
//
// The page shows the text an assistant reads on its next call, from the engine's own field on the view
// (ModelPlanView.AssistantSees; the webview's port of it is gone, lane m11/plan-gaps). This test runs the ENGINE's
// fold line over the same fixture the page's driver and node test read, and compares it byte for byte with the
// checked-in text the drives' mock engine is held to (test/plan-heart.test.mjs), so the scenes show real engine words.

using System.IO;
using System.Text.Json;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHeartFoldParityTests
    {
        private static string Fixtures()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + System.AppContext.BaseDirectory);
            return Path.Combine(dir!.FullName, "Semanticus.VSCode", "tools", "uishot", "fixtures");
        }

        // Read as LF: a Windows checkout gives the fixture CRLF endings (`* text=auto`), while OfferLine and PausedLine
        // join with "\n" on every platform. Same fix as OpParameterKindsTests (PR 396).
        private static string Fold(string name) => File.ReadAllText(Path.Combine(Fixtures(), name)).Replace("\r\n", "\n");

        private static ModelPlanView Queue(string state)
        {
            var wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
            var view = JsonSerializer.Deserialize<ModelPlanView>(File.ReadAllText(Path.Combine(Fixtures(), "plan-heart-queue.json")), wire)!;
            view.AssistantQueue = state;
            return view;
        }

        [Fact]
        public void The_page_fixture_folds_to_the_text_the_page_shows_when_the_switch_is_on()
        {
            var line = PlanTaskQueue.OfferLine(PlanOffer.From(Queue(PlanAssistantQueue.On)));
            Assert.Equal(Fold("plan-heart-fold-on.txt"), line);
        }

        [Fact]
        public void The_page_fixture_folds_to_the_paused_text_when_the_switch_is_paused()
        {
            var offer = PlanOffer.From(Queue(PlanAssistantQueue.Paused));
            var line = PlanTaskQueue.PausedLine(offer.Claimed.ConvertAll(t => t.Title));
            Assert.Equal(Fold("plan-heart-fold-paused.txt"), line);
        }
    }
}
