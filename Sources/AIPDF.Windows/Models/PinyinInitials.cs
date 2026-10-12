// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T5 汉字 → 拼音首字母（仅覆盖本应用命令标题/工具名/分类里实际出现的字）。
//
// 为什么不引第三方拼音库：为 130 个字的映射引入依赖（且需要联网还原）不划算。
// 覆盖集由 catalog.json 与命令注册表的实际用字生成，并有单测保证
// 「每个注册命令都能用其标题的拼音首字母命中」——新增命令若用到表外的字，单测会失败。
using System;
using System.Collections.Generic;
using System.Text;

namespace AIPDF.Windows.Models;

/// <summary>汉字 → 拼音首字母（覆盖本应用命令用字；表外用字按「跳过」处理）。</summary>
public static class PinyinInitials
{
    /// <summary>按首字母分组的汉字表（人工复核友好；共 130 字，见单测）。</summary>
    private static readonly Dictionary<char, string> Table = BuildTable(
    [
        KeyValuePair.Create('a', "安"),
        KeyValuePair.Create('b', "保别并板标比笔编表闭"),
        KeyValuePair.Create('c', "出拆程藏裁除"),
        KeyValuePair.Create('d', "单大定对度当打档读"),
        KeyValuePair.Create('f', "分复放翻"),
        KeyValuePair.Create('g', "关工格盖高"),
        KeyValuePair.Create('h', "划化合换护"),
        KeyValuePair.Create('j', "久件剪加建矩箭解记较辑"),
        KeyValuePair.Create('k', "可宽开"),
        KeyValuePair.Create('l', "亮令列朗流理量"),
        KeyValuePair.Create('m', "名命密描模码面"),
        KeyValuePair.Create('n', "内"),
        KeyValuePair.Create('p', "屏批排片配"),
        KeyValuePair.Create('q', "全前取签"),
        KeyValuePair.Create('r', "容"),
        KeyValuePair.Create('s', "删双式手扫搜收水示索缩设识适锁"),
        KeyValuePair.Create('t', "图头提添跳退"),
        KeyValuePair.Create('w', "为文"),
        KeyValuePair.Create('x', "下修写小形新旋线许选"),
        KeyValuePair.Create('y', "于优印压圆永演译页"),
        KeyValuePair.Create('z', "中作制字整章置转遮"),
    ]);

    /// <summary>表内汉字总数（单测据此锁定覆盖面）。</summary>
    public static int CoveredCharacterCount => Table.Count;

    /// <summary>某字是否在表内。</summary>
    public static bool Covers(char value) => Table.ContainsKey(value);

    /// <summary>
    /// 取字符串的拼音首字母串（仅取汉字；非汉字与表外字跳过）。
    /// 例：<c>全文搜索</c> → <c>qwss</c>。
    /// </summary>
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Table.TryGetValue(ch, out var initial))
            {
                builder.Append(initial);
            }
        }

        return builder.ToString();
    }

    private static Dictionary<char, string> BuildTable(IEnumerable<KeyValuePair<char, string>> groups)
    {
        var table = new Dictionary<char, string>();
        foreach (var (initial, characters) in groups)
        {
            foreach (var ch in characters)
            {
                if (!table.TryAdd(ch, initial.ToString()))
                {
                    throw new InvalidOperationException($"拼音表重复字：{ch}");
                }
            }
        }

        return table;
    }
}
