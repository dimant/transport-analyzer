namespace TransportAnalyzer.Ingestion.Fars;

/// <summary>Bound from configuration section <c>Sources:fars</c>.</summary>
public sealed class FarsOptions
{
    /// <summary>Download URL of one year's national CSV zip; <c>{year}</c> is replaced by the data year.</summary>
    public string UrlTemplate { get; set; } =
        "https://static.nhtsa.gov/nhtsa/downloads/FARS/{year}/National/FARS{year}NationalCSV.zip";

    /// <summary>Years ingested when a run does not name any.</summary>
    public int[] DefaultYears { get; set; } = [2019, 2020, 2021, 2022, 2023];
}
