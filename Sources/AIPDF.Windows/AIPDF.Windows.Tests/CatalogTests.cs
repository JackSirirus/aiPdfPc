using System.Text;
using AIPDF.Windows.Models;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 工具目录加载与分组测试。基准来自 backend/catalog.json：
/// 32 个工具、6 个分组（整理页面 6 / 优化文件 3 / 格式转换 11 / 编辑内容 6 / 安全保护 5 / 工作流程 1）。
/// </summary>
public sealed class CatalogTests
{
    private static IReadOnlyList<Tool> LoadCatalog() => ToolCatalog.Load();

    [Fact]
    public void Load_Returns32Tools()
    {
        var tools = LoadCatalog();

        Assert.Equal(32, tools.Count);
    }

    [Fact]
    public void Load_IdsAreUniqueAndCoreTextPresent()
    {
        var tools = LoadCatalog();

        var ids = tools.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Category));
        });
    }

    [Fact]
    public void GroupByCategory_ProducesSixGroupsInCatalogOrder()
    {
        var groups = ToolCatalog.GroupByCategory(LoadCatalog());

        Assert.Equal(6, groups.Count);
        Assert.Equal(
            new[] { "整理页面", "优化文件", "格式转换", "编辑内容", "安全保护", "工作流程" },
            groups.Select(g => g.Name).ToArray());
        Assert.Equal(32, groups.Sum(g => g.Tools.Count));
    }

    [Fact]
    public void GroupByCategory_MatchesExpectedDistribution()
    {
        var expected = new Dictionary<string, int>
        {
            ["整理页面"] = 6,
            ["优化文件"] = 3,
            ["格式转换"] = 11,
            ["编辑内容"] = 6,
            ["安全保护"] = 5,
            ["工作流程"] = 1,
        };

        var groups = ToolCatalog.GroupByCategory(LoadCatalog());

        Assert.Equal(expected, groups.ToDictionary(g => g.Name, g => g.Tools.Count));
    }

    [Fact]
    public void GroupByCategory_KeepsCatalogOrderInsideEachGroup()
    {
        var tools = LoadCatalog();
        var groups = ToolCatalog.GroupByCategory(tools);

        foreach (var group in groups)
        {
            var expectedIds = tools.Where(t => t.Category == group.Name).Select(t => t.Id).ToArray();
            Assert.Equal(expectedIds, group.Tools.Select(t => t.Id).ToArray());
        }
    }

    [Fact]
    public void Decode_PrefersUtf8()
    {
        _ = ToolCatalog.CatalogFileName; // 触发静态构造函数，注册 CodePages 编码提供程序

        var text = "合并 PDF — 把多份文件，整理成一份";

        Assert.Equal(text, ToolCatalog.Decode(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Decode_FallsBackToGbkCompatibleEncoding()
    {
        _ = ToolCatalog.CatalogFileName; // 触发静态构造函数，注册 CodePages 编码提供程序

        var text = "永久遮盖：从导出文件中移除敏感内容";
        var bytes = Encoding.GetEncoding("GB18030").GetBytes(text); // GB18030 向下兼容 GBK

        Assert.Equal(text, ToolCatalog.Decode(bytes));
    }
}
