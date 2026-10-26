using RouteBaseWeb.Services.Sql;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

public class SqlBatchTests
{
    [Fact]
    public void Token_ReplacesSinglePlaceholder()
    {
        var b = new SqlBatch("SELECT * FROM t WHERE /*{filter}*/");
        b.Token("filter", "id = 1");
        Assert.Equal("SELECT * FROM t WHERE id = 1", b.Sql);
    }

    [Fact]
    public void Token_ReplacesAllOccurrencesOfSameToken()
    {
        var b = new SqlBatch("/*{x}*/ AND /*{x}*/");
        b.Token("x", "1=1");
        Assert.Equal("1=1 AND 1=1", b.Sql);
    }

    [Fact]
    public void Token_UnknownToken_LeavesBodyUnchanged()
    {
        var b = new SqlBatch("SELECT 1");
        b.Token("missing", "value");
        Assert.Equal("SELECT 1", b.Sql);
    }

    [Fact]
    public void Token_ReturnsItself_ForChaining()
    {
        var b = new SqlBatch("/*{a}*/");
        var result = b.Token("a", "1");
        Assert.Same(b, result);
    }

    [Fact]
    public void Param_RegistersParameter_ReturnsName()
    {
        var b = new SqlBatch("SELECT $val");
        var name = b.Param("$val", 42);
        Assert.Equal("$val", name);
        Assert.Single(b.Params);
        Assert.Equal("$val", b.Params[0].ParameterName);
        Assert.Equal(42, b.Params[0].Value);
    }

    [Fact]
    public void Param_DuplicateName_SecondCallIgnored_FirstValueKept()
    {
        var b = new SqlBatch("SELECT $x");
        b.Param("$x", "first");
        b.Param("$x", "second");
        Assert.Single(b.Params);
        Assert.Equal("first", b.Params[0].Value);
    }

    [Fact]
    public void ParamList_EmptyValues_ReturnsEmptyString_NoParams()
    {
        var b = new SqlBatch("SELECT 1");
        var result = b.ParamList("$f", Enumerable.Empty<string>());
        Assert.Equal(string.Empty, result);
        Assert.Empty(b.Params);
    }

    [Fact]
    public void ParamList_ThreeValues_GeneratesNumberedNamesAndRegistersAll()
    {
        var b = new SqlBatch("SELECT 1");
        var result = b.ParamList("$f", new[] { "a", "b", "c" });
        Assert.Equal("$f0,$f1,$f2", result);
        Assert.Equal(3, b.Params.Count);
        Assert.Equal("$f0", b.Params[0].ParameterName);
        Assert.Equal("a", b.Params[0].Value);
        Assert.Equal("$f1", b.Params[1].ParameterName);
        Assert.Equal("b", b.Params[1].Value);
        Assert.Equal("$f2", b.Params[2].ParameterName);
        Assert.Equal("c", b.Params[2].Value);
    }

    [Fact]
    public void ParamList_CalledTwiceWithSamePrefix_DeduplicatesParams()
    {
        var b = new SqlBatch("SELECT 1");
        var r1 = b.ParamList("$f", new[] { "a", "b" });
        var r2 = b.ParamList("$f", new[] { "a", "b" });
        Assert.Equal("$f0,$f1", r1);
        Assert.Equal("$f0,$f1", r2);
        Assert.Equal(2, b.Params.Count);
    }
}
