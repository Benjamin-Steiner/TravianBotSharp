namespace MainCore.Test.Parsers
{
    public class QuestParser : BaseParser
    {
        private const string QuestClaimable = "Parsers/Quest/QuestClaimable.html";
        private const string QuestNotClaimable = "Parsers/Quest/QuestNotClaimable.html";
        private const string QuestPage = "Parsers/Quest/QuestPage.html";

        [Theory]
        [InlineData(QuestClaimable)]
        [InlineData(QuestNotClaimable)]
        public void GetQuestMaster(string file)
        {
            _html.Load(file);
            var actual = MainCore.Parsers.QuestParser.GetQuestMaster(_html);
            actual.ShouldNotBeNull();
        }

        [Theory]
        [InlineData(QuestClaimable, true)]
        [InlineData(QuestNotClaimable, false)]
        public void IsQuestClaimable(string file, bool expected)
        {
            _html.Load(file);
            var actual = MainCore.Parsers.QuestParser.IsQuestClaimable(_html);
            actual.ShouldBe(expected);
        }

        [Fact]
        public void GetQuestCollectButton()
        {
            _html.Load(QuestPage);
            var result = MainCore.Parsers.QuestParser.GetQuestCollectButton(_html);
            result.ShouldNotBeNull();
        }

        [Fact]
        public void IsQuestPage()
        {
            _html.Load(QuestPage);
            var result = MainCore.Parsers.QuestParser.IsQuestPage(_html);
            result.ShouldBeTrue();
        }

        [Fact]
        public void GetEasiestQuest()
        {
            _html.Load(QuestPage);
            var quest = MainCore.Parsers.QuestParser.GetEasiestQuest(_html);
            quest.ShouldNotBeNull();
            quest!.QuestId.ShouldBe("buildingProgress_18_1");
            quest.Scope.ShouldBe(MainCore.Parsers.QuestParser.QuestScope.Village);
        }
    }
}