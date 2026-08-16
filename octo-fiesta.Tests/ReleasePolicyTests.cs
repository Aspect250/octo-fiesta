using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class ReleasePolicyTests
{
    [Fact]
    public void StripAlbumQualifiers_AnniversaryEdition()
    {
        var title = "Dr. Feelgood (35th Anniversary / Remastered 2024)";
        var stripped = ReleasePolicy.StripAlbumQualifiers(title);
        Assert.Equal("Dr. Feelgood", stripped);
    }

    [Fact]
    public void StripAlbumQualifiers_ExpandedEdition()
    {
        var title = "Hotel California (40th Anniversary Expanded Edition)";
        var stripped = ReleasePolicy.StripAlbumQualifiers(title);
        Assert.Equal("Hotel California", stripped);
    }

    [Fact]
    public void JunkTerms_IncludePlurals_AndPlainTitleCheck()
    {
        Assert.True(ReleasePolicy.IsPlainAlbumTitle("Dr. Feelgood"));
        Assert.False(ReleasePolicy.IsPlainAlbumTitle("Dr. Feelgood (35th Anniversary / Remastered 2024)"));
        Assert.True(ReleasePolicy.HasJunkTerm("Modjo Remixes"));
        Assert.True(ReleasePolicy.HasJunkTerm("Modjo Remix"));
    }
}
