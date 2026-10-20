using ViennaAtomicClock;
using Xunit;

namespace Freedom.Tests;

public sealed class TimetableCatalogTests
{
    private const string CatalogJson = """
        {
          "data": {
            "elements": [
              { "type": 1, "id": 401, "name": "4BHITS", "longName": "4BHITS", "dids": [11] },
              { "type": 1, "id": 402, "name": "3AHITS", "longName": "3AHITS", "dids": [11] },
              { "type": 1, "id": 501, "name": "2AHMBT", "longName": "2AHMBT", "dids": [4] },
              { "type": 2, "id": 99, "name": "Teacher", "longName": "Not a class" }
            ],
            "filters": [
              {
                "type": 10,
                "property": "departmentId",
                "elements": [
                  { "id": 11, "name": "Informationstechnologie" },
                  { "id": 4, "label": "Maschinenbau" }
                ]
              }
            ]
          }
        }
        """;

    [Fact]
    public void Page_config_is_split_into_departments_and_their_classes()
    {
        var catalog = WebUntisTimetable.ParseCatalog(CatalogJson);

        Assert.Equal(new[] { "Informationstechnologie", "Maschinenbau" }, catalog.Departments.Select(item => item.Name));
        Assert.Equal(new[] { "2AHMBT", "3AHITS", "4BHITS" }, catalog.Classes.Select(item => item.Name));
        Assert.Equal(11, catalog.Classes.Single(item => item.Name == "4BHITS").DepartmentIds.Single());
        Assert.DoesNotContain(catalog.Classes, item => item.Id == 99);
    }

    [Fact]
    public void Timetable_data_remembers_which_class_its_entries_belong_to()
    {
        var timetable = new TimetableData { ClassName = "1AHITS" };

        Assert.Equal("1AHITS", timetable.ClassName);
        Assert.Equal("4BHITS", new TimetableData().ClassName);
    }

    [Fact]
    public void Every_class_picker_text_exists_in_every_language()
    {
        var keys = new[]
        {
            "schedule.class.tip", "schedule.department", "schedule.class",
            "schedule.classes.loading", "schedule.classes.error"
        };

        foreach (var key in keys)
        {
            Assert.True(Strings.Table.TryGetValue(key, out var texts), key);
            Assert.Equal(Loc.Languages.Length, texts!.Length);
            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        }
    }
}
