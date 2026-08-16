using System.Text;
using System.Text.RegularExpressions;
using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Central content policy for what may be surfaced in search results and what may be
/// auto-downloaded (hermes fork). Search-time filtering (SubsonicModelMapper) and
/// download-time gating (BaseDownloadService) share these rules so the two layers
/// cannot drift apart.
///
/// Policy (user-binding, 16 Aug 2026): original studio albums only. No editions
/// (deluxe/anniversary/bonus/expanded/special/legacy), no live/remix/demo albums, no
/// compilations or best-ofs as download targets. Remastered-only titles are demoted
/// (tolerated when no plain equivalent exists), never hard-skipped.
/// </summary>
public static class ReleasePolicy
{
    /// <summary>
    /// Track/album junk markers: live performances, remixes, mashups, acoustic/unplugged
    /// sessions, demos, radio/club/extended edits. Word-boundary aware so "Alive" or
    /// "remixed" are not false positives, while "(Live)" / "(Demo)" / "Remixes" are
    /// (plural forms included — "Modjo Remixes" must be caught).
    /// </summary>
    public static readonly Regex JunkTermRegex = new(
        @"\b(live|remix(?:es)?|mashup(?:s)?|acoustic|unplugged|demo(?:s)?|radio edit|club mix|extended|session(?:s)?|jam in the van)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Additional album-level junk: greatest-hits/best-of collections, classics,
    /// soundtracks, "various artists" comps, "Now That's What I Call…", "The Essential…".
    /// </summary>
    public static readonly Regex AlbumJunkTermRegex = new(
        @"\b(greatest hits|best of|the hits|classics|soundtrack|various artists|now that'?s what i call|the essential|hits)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Edition qualifiers — hard-rejected as download targets. Also used (together with
    /// the junk/remaster terms) when stripping qualifiers to recover a base title.
    /// </summary>
    public static readonly Regex EditionTermRegex = new(
        @"\b(deluxe|anniversary|bonus|expanded|special|legacy|edition)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Remaster markers — demote-only (never a hard skip; a plain remastered studio album
    /// is tolerated when it is the only version available).
    /// </summary>
    public static readonly Regex RemasterTermRegex = new(
        @"\bremaster(?:ed)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches parenthesized/bracketed qualifier groups, e.g. "(Bonus Edition)",
    /// "[Remastered 2024]", "(35th Anniversary / Remastered)", "(40th Anniversary Expanded Edition)".
    /// </summary>
    private static readonly Regex QualifierGroupRegex = new(
        @"[\(\[]([^\)\]]*?(deluxe|anniversary|bonus|expanded|special|legacy|edition|remaster(?:ed)?|live)[^\)\]]*?)[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a trailing dash-separated qualifier phrase, e.g. " - Deluxe Edition",
    /// " - 35th Anniversary Edition", " - Live at Wembley".
    /// </summary>
    private static readonly Regex TrailingQualifierRegex = new(
        @"\s-\s+[^-]*(deluxe|anniversary|bonus|expanded|special|legacy|edition|remaster(?:ed)?|live)[^-]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Junk-phrase removal for re-resolution queries: strips "Remixes", "Live", etc. from a
    /// base title ("Modjo Remixes" → "Modjo"). Conservative: never returns empty — falls
    /// back to the input when nothing meaningful survives.
    /// </summary>
    private static readonly Regex JunkPhraseRemovalRegex = new(
        @"\b(live|remix(?:es)?|mashup(?:s)?|acoustic|unplugged|demo(?:s)?|radio edit|club mix|extended|session(?:s)?|jam in the van)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the haystack carries a track/album junk marker (live/remix/demo/…).
    /// </summary>
    public static bool HasJunkTerm(string? haystack)
    {
        return !string.IsNullOrWhiteSpace(haystack) && JunkTermRegex.IsMatch(haystack);
    }

    /// <summary>
    /// True when the title carries a compilation-style marker (greatest hits, best of,
    /// soundtracks, "hits", "various artists", "now that's what i call", "the essential").
    /// </summary>
    public static bool HasAlbumJunkTerm(string? title)
    {
        return !string.IsNullOrWhiteSpace(title) && AlbumJunkTermRegex.IsMatch(title);
    }

    /// <summary>
    /// True when the title carries an edition qualifier (deluxe/anniversary/bonus/expanded/
    /// special/legacy/edition). Remaster markers are deliberately NOT included here.
    /// </summary>
    public static bool IsEditionAlbumTitle(string? title)
    {
        return !string.IsNullOrWhiteSpace(title) && EditionTermRegex.IsMatch(title);
    }

    /// <summary>
    /// True when the title carries a remaster marker but no junk and no edition qualifier —
    /// the only "demote-only" case in the policy.
    /// </summary>
    public static bool IsRemasterOnlyAlbumTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }
        return RemasterTermRegex.IsMatch(title)
            && !JunkTermRegex.IsMatch(title)
            && !AlbumJunkTermRegex.IsMatch(title)
            && !EditionTermRegex.IsMatch(title);
    }

    /// <summary>
    /// Song-level junk check against title and album, WITHOUT the query exemption
    /// (used by the download gate — octo-sync's plain "{artist} {title}" queries must
    /// never bypass the policy).
    /// </summary>
    public static bool IsJunkTrackTitle(string? title, string? album)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(album))
        {
            return false;
        }
        return JunkTermRegex.IsMatch(title + "\n" + album);
    }

    /// <summary>
    /// Song-level junk check with the search query exemption: an explicit search for
    /// "Hotel California (Live)" must still return live results, so a matched term that
    /// also appears in the query does not drop the song.
    /// </summary>
    public static bool IsJunkTrackWithQueryExemption(string? title, string? album, string? query)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        var haystack = title + "\n" + album;
        var matches = JunkTermRegex.Matches(haystack);
        if (matches.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (Match match in matches)
            {
                if (query.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Strips edition/live qualifiers from an album title to recover its base title:
    /// "Dr. Feelgood (35th Anniversary / Remastered 2024)" → "Dr. Feelgood",
    /// "Hotel California (40th Anniversary Expanded Edition)" → "Hotel California".
    /// </summary>
    public static string StripAlbumQualifiers(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title ?? string.Empty;
        }

        var result = title;

        // Repeated passes handle stacked groups like "(Deluxe) [Bonus Tracks]".
        bool changed;
        do
        {
            changed = false;
            result = QualifierGroupRegex.Replace(result, _ =>
            {
                changed = true;
                return " ";
            });
            result = result.Trim();
        } while (changed);

        // Trailing dash-separated phrases: "Some Album - Deluxe Edition" -> "Some Album".
        var dashMatch = TrailingQualifierRegex.Match(result);
        if (dashMatch.Success)
        {
            result = result[..dashMatch.Index].TrimEnd();
        }

        return result.Trim();
    }

    /// <summary>
    /// Removes junk phrases from a base title for re-resolution queries
    /// ("Modjo Remixes" → "Modjo", "Greatest Hits II" → "Greatest"). Falls back to the
    /// input when nothing meaningful survives the removal.
    /// </summary>
    public static string StripJunkTerms(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title ?? string.Empty;
        }

        var result = JunkPhraseRemovalRegex.Replace(title, " ");
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        if (result.Length < 4)
        {
            return title.Trim();
        }
        return result;
    }

    /// <summary>
    /// True when the title carries no edition/live qualifier at all.
    /// </summary>
    public static bool IsPlainAlbumTitle(string? title)
    {
        return string.Equals(
            StripAlbumQualifiers(title).Trim(),
            title?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the album is a compilation: Deezer record_type == "compilation", a
    /// "Various Artists" artist, a compilation-style title, or (when the tracklist is
    /// present) more than one distinct artist across its tracks.
    /// </summary>
    public static bool IsCompilationAlbum(Album album)
    {
        if (!string.IsNullOrWhiteSpace(album.ReleaseType)
            && album.ReleaseType.Equals("compilation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(album.Artist)
            && album.Artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (HasAlbumJunkTerm(album.Title))
        {
            return true;
        }

        if (album.Songs.Count > 1)
        {
            var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in album.Songs)
            {
                if (!string.IsNullOrWhiteSpace(song.Artist))
                {
                    artists.Add(song.Artist.Trim());
                }
            }
            if (artists.Count > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates an album against the download policy.
    /// </summary>
    public static AlbumDownloadPolicy EvaluateAlbumForDownload(Album album)
    {
        if (IsCompilationAlbum(album))
        {
            return AlbumDownloadPolicy.HardReject;
        }

        if (HasJunkTerm(album.Title) || HasAlbumJunkTerm(album.Title))
        {
            return AlbumDownloadPolicy.HardReject;
        }

        if (IsEditionAlbumTitle(album.Title))
        {
            return AlbumDownloadPolicy.HardReject;
        }

        if (IsRemasterOnlyAlbumTitle(album.Title))
        {
            return AlbumDownloadPolicy.DemoteOnly;
        }

        return AlbumDownloadPolicy.Accept;
    }

    /// <summary>
    /// True when the release type is a single or EP (track-only download — Album mode applies
    /// to real albums only).
    /// </summary>
    public static bool IsSingleOrEp(string? releaseType)
    {
        return !string.IsNullOrWhiteSpace(releaseType)
            && (releaseType.Equals("single", StringComparison.OrdinalIgnoreCase)
                || releaseType.Equals("ep", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalized artist comparison (case-insensitive, quote/dash normalization).
    /// "Various Artists" never matches.
    /// </summary>
    public static bool ArtistMatches(string? candidate, string? reference)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }
        if (candidate.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var left = StringNormalizer.NormalizeForComparison(candidate);
        var right = StringNormalizer.NormalizeForComparison(reference);
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Outcome of evaluating an album against the download policy.
/// </summary>
public enum AlbumDownloadPolicy
{
    /// <summary>Safe to download as-is.</summary>
    Accept,

    /// <summary>Remastered-only title: prefer a plain equivalent; fall back to this album if none exists.</summary>
    DemoteOnly,

    /// <summary>Never download (edition, compilation, live/remix/junk). Re-resolve or skip.</summary>
    HardReject,
}
