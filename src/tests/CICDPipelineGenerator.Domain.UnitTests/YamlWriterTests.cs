using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class YamlWriterTests
{
    [Theory]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("No")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("1.0")]
    [InlineData("22")]
    [InlineData("0o17")]
    [InlineData("a: b")]
    [InlineData("value # not a comment")]
    [InlineData("- item")]
    [InlineData("*alias")]
    [InlineData("'quoted'")]
    [InlineData("it's")]
    [InlineData("${{ vars.AZURE_CLIENT_ID }}")]
    [InlineData("$(Build.SourceVersion)")]
    [InlineData("api://AzureADTokenExchange")]
    [InlineData(" padded ")]
    [InlineData("")]
    public void Text_reads_back_as_the_same_text(string value)
    {
        var yaml = YamlWriter.Write(new YMap { { "key", value }, { "list", new YSeq([value]) } });

        var root = Parse(yaml);
        var scalar = (YamlScalarNode)root.Children[new YamlScalarNode("key")];
        Assert.Equal(value, scalar.Value);

        // Text that YAML would read as a boolean, null or number is quoted, so every parser sees text.
        if (YamlWriter.NeedsQuotes(value, isKey: false))
        {
            Assert.NotEqual(YamlDotNet.Core.ScalarStyle.Plain, scalar.Style);
        }

        Assert.Equal(value, ((YamlScalarNode)((YamlSequenceNode)root.Children[new YamlScalarNode("list")]).Children[0]).Value);
    }

    [Fact]
    public void Multiline_text_is_a_literal_block_that_keeps_every_line()
    {
        const string script = "echo one\n  indented: yes\n\necho \"two\" # comment";
        var yaml = YamlWriter.Write(new YMap { { "run", script }, { "steps", new YSeq([script, "  starts with spaces\nnext"]) } });

        Assert.Contains("run: |\n", yaml);
        var root = Parse(yaml);
        Assert.Equal(script + "\n", ((YamlScalarNode)root.Children[new YamlScalarNode("run")]).Value);
        var steps = (YamlSequenceNode)root.Children[new YamlScalarNode("steps")];
        Assert.Equal("  starts with spaces\nnext\n", ((YamlScalarNode)steps.Children[1]).Value);
    }

    [Fact]
    public void Nested_maps_and_lists_keep_their_order_and_skip_null_values()
    {
        var yaml = YamlWriter.Write(new YMap
        {
            { "b", "first" },
            { "a", new YSeq { new YMap { { "uses", "x" }, { "with", new YMap { { "k", true }, { "n", 3 } } } }, new YMap(), new YSeq() } },
            { "skipped", (string?)null },
            { "empty", new YMap() },
        }, ["header line"]);

        Assert.StartsWith("# header line\n", yaml);
        Assert.DoesNotContain("skipped", yaml);
        var root = Parse(yaml);
        Assert.Equal(["b", "a", "empty"], root.Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        var item = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("a")]).Children[0];
        var with = (YamlMappingNode)item.Children[new YamlScalarNode("with")];
        Assert.Equal(YamlDotNet.Core.ScalarStyle.Plain, ((YamlScalarNode)with.Children[new YamlScalarNode("k")]).Style);
        Assert.Equal("3", ((YamlScalarNode)with.Children[new YamlScalarNode("n")]).Value);
    }

    internal static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }
}
