using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashDocumentValidationTests
{
 [Theory]
 [InlineData("scenes/a.json","{\"markers\":[{\"seconds\":1}]}","marker-time")]
 [InlineData("scenes/a.json","{\"markers\":[{\"seconds\":\"NaN\"}]}","marker-time")]
 [InlineData("scenes/a.json","{\"markers\":[{\"seconds\":\"-1\"}]}","marker-time")]
 [InlineData("scenes/a.json","{\"markers\":[{\"seconds\":\"3\",\"end_seconds\":\"2\"}]}","marker-range")]
 [InlineData("scenes/a.json","{\"markers\":[{\"seconds\":\"1,25\"}]}","marker-time")]
 [InlineData("scenes/a.json","{\"movies\":[{\"movieName\":9}]}","string")]
 [InlineData("scenes/a.json","{\"organized\":\"true\"}","organized")]
 [InlineData("scenes/a.json","{\"rating\":101}","rating")]
 [InlineData("scenes/a.json","{\"tags\":[9]}","array-item")]
 [InlineData("images/a.json","{\"galleries\":[{}]}","gallery-reference")]
 [InlineData("performers/a.json","{\"name\":\"A\",\"height\":170}","string")]
 [InlineData("performers/a.json","{\"name\":\"A\",\"aliases\":3}","aliases")]
 [InlineData("files/a.json","{\"type\":\"video\",\"path\":\"a\",\"size\":\"9\"}","file-size")]
 [InlineData("files/a.json","{\"type\":\"video\",\"path\":\"a\",\"size\":9,\"duration\":\"3\"}","file-number")]
 [InlineData("files/a.json","{\"type\":\"unknown\",\"path\":\"a\",\"size\":0}","file-type")]
 [InlineData("tags/a.json","{\"name\":\"A\",\"name\":\"B\"}","duplicate-property")]
 [InlineData("tags/a.json","{\"name\":\"A\",\"custom_fields\":{\"x\":1,\"x\":2}}","duplicate-property")]
 [InlineData("tags/a.json","[]","object")]
 public void InvalidSourceFieldsProduceTheirExpectedError(string relative,string json,string code)
 {
  using var document=JsonDocument.Parse(json);
  var kind=StashExport.Sections[relative.Split('/')[0]];
  var result=StashExport.ValidateDocument(kind,document.RootElement);
  Assert.Contains(result.Issues,issue=>issue.Severity==StashIssueSeverity.Error&&(issue.Code==code||code=="marker-time"&&issue.Code=="string"));
 }
}
