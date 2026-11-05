namespace TransportAnalyzer.Ingestion.Faa;

/// <summary>FAA General Aviation and Part 135 Activity Survey, chapter 1. The file name changes every survey year;
/// update <see cref="Url"/> (config <c>Sources:faa-ga-survey:Url</c>) when a new edition is published.</summary>
public sealed class FaaGaSurveyOptions
{
    public string Url { get; set; } =
        "https://www.faa.gov/data_research/aviation_data_statistics/general_aviation/cy2024/2024GASurveyCh1.xlsx";

    /// <summary>Worksheets staged and mapped (table 1.3 by aircraft type, 1.4 by use).</summary>
    public string[] Sheets { get; set; } = ["1.3", "1.4"];

    /// <summary>Mapping file name under <c>{Data:Root}/mappings</c>.</summary>
    public string MappingFile { get; set; } = "faa-ga-survey.csv";

    /// <summary>Factor from table units to hours (the tables are in thousands of hours).</summary>
    public double UnitMultiplier { get; set; } = 1000;
}
