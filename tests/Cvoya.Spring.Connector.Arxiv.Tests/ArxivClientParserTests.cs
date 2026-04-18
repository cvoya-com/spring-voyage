// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Tests;

using System.Xml.Linq;

using Cvoya.Spring.Connector.Arxiv;

using Shouldly;

using Xunit;

public class ArxivClientParserTests
{
    private const string SampleFeed = """
    <feed xmlns="http://www.w3.org/2005/Atom" xmlns:arxiv="http://arxiv.org/schemas/atom">
      <entry>
        <id>http://arxiv.org/abs/2401.12345v2</id>
        <updated>2024-02-03T12:34:56Z</updated>
        <published>2024-01-15T10:00:00Z</published>
        <title>Learning to Prune with Provable Guarantees
        </title>
        <summary>
          We present a new pruning algorithm that
          generalises across model families.
        </summary>
        <author><name>Ada Lovelace</name></author>
        <author><name>Alan Turing</name></author>
        <arxiv:primary_category term="cs.LG"/>
        <category term="cs.LG"/>
        <category term="stat.ML"/>
        <link href="http://arxiv.org/abs/2401.12345v2" rel="alternate"/>
        <link href="http://arxiv.org/pdf/2401.12345v2" title="pdf"/>
        <arxiv:doi>10.1234/demo</arxiv:doi>
      </entry>
    </feed>
    """;

    [Fact]
    public void ParseFeed_CanonicalisesIdAndNormalisesWhitespace()
    {
        var doc = XDocument.Parse(SampleFeed);

        var entries = ArxivClient.ParseFeed(doc);

        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Id.ShouldBe("2401.12345");
        entry.Title.ShouldBe("Learning to Prune with Provable Guarantees");
        entry.Summary.ShouldBe("We present a new pruning algorithm that generalises across model families.");
        entry.Authors.ShouldBe(new[] { "Ada Lovelace", "Alan Turing" });
        entry.PrimaryCategory.ShouldBe("cs.LG");
        entry.Categories.ShouldBe(new[] { "cs.LG", "stat.ML" });
        entry.AbsUrl.ShouldBe("http://arxiv.org/abs/2401.12345v2");
        entry.PdfUrl.ShouldBe("http://arxiv.org/pdf/2401.12345v2");
        entry.Doi.ShouldBe("10.1234/demo");
    }

    [Fact]
    public void BuildSearchQuery_IncludesCategoriesAndYearWindow()
    {
        var q = ArxivClient.BuildSearchQuery(
            "pruning",
            new[] { "cs.LG", "stat.ML" },
            yearFrom: 2023,
            yearTo: 2024);

        q.ShouldContain("all:pruning");
        q.ShouldContain("cat:cs.LG");
        q.ShouldContain("cat:stat.ML");
        q.ShouldContain("submittedDate:[2023");
        q.ShouldContain("2024");
    }

    [Fact]
    public void BuildSearchQuery_RejectsEmptyQuery()
    {
        Should.Throw<ArgumentException>(() =>
            ArxivClient.BuildSearchQuery(string.Empty, null, null, null));
    }
}