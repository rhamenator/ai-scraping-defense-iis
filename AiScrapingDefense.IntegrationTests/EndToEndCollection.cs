using Xunit;

namespace AiScrapingDefense.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class EndToEndCollection : ICollectionFixture<DefenseStackFixture>
{
    public const string Name = "end-to-end";
}
