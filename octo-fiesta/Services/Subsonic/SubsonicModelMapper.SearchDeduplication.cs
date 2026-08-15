using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.Subsonic;

public partial class SubsonicModelMapper
{
    /// <summary>
    /// Fork overload: filters the external results with the fork's dedupe rules, then
    /// delegates to the upstream merge so upstream changes to it keep applying.
    /// </summary>
    public (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResults(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult,
        List<ExternalPlaylist> externalPlaylists,
        IReadOnlyDictionary<string, LocalSongMapping>? mappings,
        bool isJson,
        string? query = null)
    {
        var localSongIds = new HashSet<string>(StringComparer.Ordinal);
        var localSongKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in localSongs)
        {
            if (LocalField(song, "id") is { Length: > 0 } id)
            {
                localSongIds.Add(id);
            }
            var key = BuildSongKey(LocalField(song, "artist"), LocalField(song, "title"));
            if (key != null)
            {
                localSongKeys.Add(key);
            }
        }

        var localAlbumKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var album in localAlbums)
        {
            // Subsonic uses "name" for album titles; some clients expose "title" too.
            var key = BuildAlbumKey(LocalField(album, "artist"), LocalField(album, "name") ?? LocalField(album, "title"));
            if (key != null)
            {
                localAlbumKeys.Add(key);
            }
        }

        var localArtistKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artist in localArtists)
        {
            var name = LocalField(artist, "name");
            if (!string.IsNullOrEmpty(name))
            {
                localArtistKeys.Add(StringNormalizer.CreateArtistComparisonKey(name));
            }
        }

        // Artists are left out here: the fork matches them case-sensitively, which keeps
        // artists upstream's case-insensitive check would drop, so they are appended below.
        var filtered = new SearchResult
        {
            Songs = externalResult.Songs
                .Where(s => !ShouldDropExternalSong(s, mappings, localSongIds, localSongKeys)
                    && !ShouldDropExternalSongByContentPolicy(s, query))
                .ToList(),
            // Content-policy pre-pass over external albums: drop Deezer compilations,
            // then prefer plain-titled albums over (Deluxe/Anniversary/...) editions.
            Albums = ApplyPlainAlbumPreference(
                    externalResult.Albums.Where(a => !IsExternalCompilationAlbum(a)))
                .Where(a => !ShouldDropExternalAlbum(a, localAlbumKeys))
                .ToList(),
        };

        // Providers (notably Qobuz) sometimes return several near-duplicate yearly snapshots
        // of the same curated list; collapse those before they are emitted as albums.
        var merged = MergeSearchResults(
            localSongs,
            localAlbums,
            localArtists,
            filtered,
            DeduplicateExternalPlaylists(externalPlaylists).ToList(),
            isJson);

        var ns = XNamespace.Get("http://subsonic.org/restapi");
        foreach (var artist in externalResult.Artists)
        {
            if (!localArtistKeys.Contains(StringNormalizer.CreateArtistComparisonKey(artist.Name)))
            {
                merged.MergedArtists.Add(isJson
                    ? _responseBuilder.ConvertArtistToJson(artist)
                    : _responseBuilder.ConvertArtistToXml(artist, ns));
            }
        }

        return merged;
    }

    /// <summary>
    /// Reads a field from a local search3 entry: a JSON dictionary or an XML element.
    /// </summary>
    private static string? LocalField(object entry, string name) => entry switch
    {
        Dictionary<string, object> dict => dict.TryGetValue(name, out var value) ? value?.ToString() : null,
        XElement element => element.Attribute(name)?.Value,
        _ => null,
    };

    /// <summary>
    /// Decides whether an external <paramref name="song"/> already has a local
    /// equivalent in the search response and should be hidden.
    /// </summary>
    private static bool ShouldDropExternalSong(
        Song song,
        IReadOnlyDictionary<string, LocalSongMapping>? mappings,
        HashSet<string> localSongIds,
        HashSet<string> localSongKeys)
    {
        // Tier 1: precise mapping check. The user has actually downloaded this exact
        // ext id via Octo Fiesta and Navidrome returned the resulting local song in
        // this same response — drop the duplicate.
        if (mappings != null
            && !string.IsNullOrEmpty(song.ExternalProvider)
            && !string.IsNullOrEmpty(song.ExternalId)
            && mappings.TryGetValue($"{song.ExternalProvider}:{song.ExternalId}", out var mapping)
            && !string.IsNullOrEmpty(mapping.LocalSubsonicId)
            && localSongIds.Contains(mapping.LocalSubsonicId))
        {
            return true;
        }

        // Tier 2: metadata fallback. Catches pre-existing library songs (no mapping row)
        // that Navidrome already surfaced for the same query.
        var key = BuildSongKey(song.Artist, song.Title);
        return key != null && localSongKeys.Contains(key);
    }

    /// <summary>
    /// Drops external albums whose normalized (artist, title) matches an album returned
    /// by the local Navidrome search. There is no album-level mapping store, so this
    /// relies on metadata alone.
    /// </summary>
    private static bool ShouldDropExternalAlbum(Album album, HashSet<string> localAlbumKeys)
    {
        var key = BuildAlbumKey(album.Artist, album.Title);
        return key != null && localAlbumKeys.Contains(key);
    }

    /// <summary>
    /// Terms that mark a track as a live/remix/alternate version the user does not
    /// want auto-pulled into the library. Word-boundary aware so "Alive" or
    /// "remixed" are not false positives, while "(Live)" / "(Demo)" are.
    /// </summary>
    private static readonly Regex ExternalSongFilterRegex = new(
        @"\b(live|remix|mashup|acoustic|unplugged|demo|radio edit|club mix|extended|session|jam in the van)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Drops external songs whose title or album carries a live/remix/alternate-version
    /// marker, UNLESS the search query itself contains the matched term — an explicit
    /// search for "Hotel California (Live)" must still return live results. Local
    /// songs are never filtered by this policy (the caller only applies it to
    /// external songs).
    /// </summary>
    private static bool ShouldDropExternalSongByContentPolicy(Song song, string? query)
    {
        if (string.IsNullOrWhiteSpace(song.Title) && string.IsNullOrWhiteSpace(song.Album))
        {
            return false;
        }

        var haystack = song.Title + "\n" + song.Album;
        var matches = ExternalSongFilterRegex.Matches(haystack);
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
    /// Matches parenthesized/bracketed qualifier groups, e.g. "(Bonus Edition)",
    /// "[Remastered 2024]", "(35th Anniversary / Remastered)".
    /// </summary>
    private static readonly Regex AlbumQualifierGroupRegex = new(
        @"[\(\[]([^\)\]]*?(deluxe|anniversary|bonus|remaster(?:ed)?|edition|live)[^\)\]]*?)[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a trailing dash-separated qualifier phrase, e.g. " - Deluxe Edition",
    /// " - 35th Anniversary Edition", " - Live at Wembley".
    /// </summary>
    private static readonly Regex AlbumTrailingQualifierRegex = new(
        @"\s-\s+[^-]*(deluxe|anniversary|bonus|remaster(?:ed)?|edition|live)[^-]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips edition/live qualifiers from an album title to recover its base title:
    /// "Dr. Feelgood (35th Anniversary / Remastered 2024)" -> "Dr. Feelgood".
    /// </summary>
    private static string StripAlbumQualifiers(string? title)
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
            result = AlbumQualifierGroupRegex.Replace(result, _ =>
            {
                changed = true;
                return " ";
            });
            result = result.Trim();
        } while (changed);

        // Trailing dash-separated phrases: "Some Album - Deluxe Edition" -> "Some Album".
        var dashMatch = AlbumTrailingQualifierRegex.Match(result);
        if (dashMatch.Success)
        {
            result = result[..dashMatch.Index].TrimEnd();
        }

        return result.Trim();
    }

    /// <summary>
    /// True when the title carries no edition/live qualifier at all.
    /// </summary>
    private static bool IsPlainAlbumTitle(string? title)
    {
        return string.Equals(
            StripAlbumQualifiers(title).Trim(),
            title?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildAlbumBaseKey(string? artist, string? title)
    {
        var artistKey = StringNormalizer.CreateComparisonKey(artist);
        var baseTitleKey = StringNormalizer.CreateComparisonKey(StripAlbumQualifiers(title));
        if (artistKey.Length == 0 || baseTitleKey.Length == 0)
        {
            return null;
        }
        return artistKey + "\u0001" + baseTitleKey;
    }

    /// <summary>
    /// Among external albums sharing the same (artist, base title), drops the
    /// qualified editions whenever a plain-titled original is present in the same
    /// result set. Groups with no plain original keep all their editions.
    /// </summary>
    private static List<Album> ApplyPlainAlbumPreference(IEnumerable<Album> albums)
    {
        var albumList = albums.ToList();
        if (albumList.Count < 2)
        {
            return albumList;
        }

        var groupsWithPlain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var album in albumList)
        {
            var key = BuildAlbumBaseKey(album.Artist, album.Title);
            if (key != null && IsPlainAlbumTitle(album.Title))
            {
                groupsWithPlain.Add(key);
            }
        }

        var result = new List<Album>(albumList.Count);
        foreach (var album in albumList)
        {
            var key = BuildAlbumBaseKey(album.Artist, album.Title);
            if (key != null && groupsWithPlain.Contains(key) && !IsPlainAlbumTitle(album.Title))
            {
                continue;
            }
            result.Add(album);
        }
        return result;
    }

    /// <summary>
    /// Drops external albums flagged by the provider as compilations (Deezer
    /// <c>record_type == compilation</c>) — multi-artist "playlists as albums"
    /// the user does not want.
    /// </summary>
    private static bool IsExternalCompilationAlbum(Album album)
    {
        return !string.IsNullOrWhiteSpace(album.ReleaseType)
            && album.ReleaseType.Equals("compilation", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildSongKey(string? artist, string? title)
    {
        var artistKey = StringNormalizer.CreateComparisonKey(artist);
        var titleKey = StringNormalizer.CreateSongTitleDedupeKey(title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
        {
            return null;
        }
        return artistKey + "\u0001" + titleKey;
    }

    private static string? BuildAlbumKey(string? artist, string? title)
    {
        var artistKey = StringNormalizer.CreateComparisonKey(artist);
        var titleKey = StringNormalizer.CreateComparisonKey(title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
        {
            return null;
        }
        return artistKey + "\u0001" + titleKey;
    }

    /// <summary>
    /// Collapses external playlists that share the same normalized
    /// (provider, name, curator) tuple. Preserves insertion order and keeps the
    /// first occurrence. Entries with an empty/whitespace <see cref="ExternalPlaylist.Name"/>
    /// are passed through untouched so we never silently merge unidentified rows.
    /// </summary>
    private static IEnumerable<ExternalPlaylist> DeduplicateExternalPlaylists(
        IEnumerable<ExternalPlaylist> playlists)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var playlist in playlists)
        {
            if (string.IsNullOrWhiteSpace(playlist.Name))
            {
                yield return playlist;
                continue;
            }

            var providerKey = NormalizePlaylistKeyPart(playlist.Provider);
            var nameKey = NormalizePlaylistKeyPart(playlist.Name);
            var curatorKey = NormalizePlaylistKeyPart(playlist.CuratorName ?? string.Empty);
            var key = providerKey + "\u0001" + nameKey + "\u0001" + curatorKey;

            if (seen.Add(key))
            {
                yield return playlist;
            }
        }
    }

    /// <summary>
    /// Normalizes a single component of the playlist dedupe key. Builds on top of
    /// <see cref="StringNormalizer.CreateComparisonKey"/> but additionally trims and
    /// collapses runs of internal whitespace so trivial padding differences in the
    /// provider's response don't keep otherwise identical playlists separate.
    /// </summary>
    private static string NormalizePlaylistKeyPart(string? input)
    {
        var normalized = StringNormalizer.CreateComparisonKey(input);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(normalized.Length);
        var prevSpace = true;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length -= 1;
        }
        return sb.ToString();
    }
    
}
