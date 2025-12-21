using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Diary.App.Models;

public class RespondTag
{
    [JsonIgnore]
    public const string AnonymousName = "**未分类**";
    
    [JsonPropertyName("name")]
    public string TagName {get; set;} = AnonymousName;
    [JsonPropertyName("total")]
    public double TagTime {get; set;}
    
    [JsonIgnore]
    public double Percent {get; set;}

    [JsonPropertyName("children")]
    public List<RespondTag> SubTags { get; set; } = new();

    [JsonIgnore] public bool IsValid => TagTime > 0;
    [JsonIgnore] public bool IsAnno => string.Compare(TagName, AnonymousName, StringComparison.Ordinal) == 0;

    public static RespondTag Null { get; } =  new(){ TagName = "没有数据！", TagTime = 0 };
}

public class RespondData
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    [JsonPropertyName("date_start")]
    public string DateStart { get; set; } = string.Empty;
    [JsonPropertyName("date_end")]
    public string DateEnd { get; set; } = string.Empty;
    [JsonPropertyName("hours")]
    public double TotalTime {get; set;}
    [JsonPropertyName("tags")]
    public List<RespondTag> Tags { get; set; } = new();

    [JsonIgnore] public string Key => $"{Username}@{Hostname}";
}