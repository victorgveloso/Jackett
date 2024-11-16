using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;
using AngleSharp.Dom;
using Jackett.Common.Utils;
using Newtonsoft.Json.Linq;
using static System.Linq.Enumerable;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Definitions
{
    public abstract class PublicBrazilianIndexerBase : IndexerBase
    {
        public PublicBrazilianIndexerBase(IIndexerConfigurationService configService, WebClient wc, Logger l,
                                          IProtectionService ps, ICacheService cs) : base(
            configService: configService, client: wc, logger: l, p: ps, cacheService: cs,
            configData: new ConfigurationData())
        {
        }

        public override string Description =>
            $"{Name} is a Public Torrent Tracker for Movies and TV Shows dubbed in Brazilian Portuguese";

        public override string Language => "pt-BR";
        public override string Type => "public";
        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                MovieSearchParams = new List<MovieSearchParam> { MovieSearchParam.Q },
                TvSearchParams = new List<TvSearchParam> { TvSearchParam.Q }
            };
            caps.Categories.AddCategoryMapping("filmes", TorznabCatType.Movies);
            caps.Categories.AddCategoryMapping("series", TorznabCatType.TV);
            return caps;
        }

        public override IIndexerRequestGenerator GetRequestGenerator() => new SimpleRequestGenerator(SiteLink);

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            await ConfigureIfOK(string.Empty, true, () => throw new Exception("Could not find releases from this URL"));
            return IndexerConfigurationStatus.Completed;
        }
    }

    public class SimpleRequestGenerator : IIndexerRequestGenerator
    {
        private readonly string _siteLink;

        public SimpleRequestGenerator(string siteLink)
        {
            _siteLink = siteLink;
        }

        public IndexerPageableRequestChain GetSearchRequests(TorznabQuery query)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            var searchUrl = $"{_siteLink}?s=";
            if (!string.IsNullOrWhiteSpace(query.SearchTerm))
                searchUrl += WebUtility.UrlEncode(query.SearchTerm.Replace(" ", "+"));

            pageableRequests.Add(new [] {new IndexerRequest(searchUrl)});

            return pageableRequests;
        }
    }

    public static class RowParsingExtensions
    {
        public static Uri ExtractMagnet(this IElement downloadButton)
        {
            var magnetLink = downloadButton.GetAttribute("href");
            var magnet = string.IsNullOrEmpty(magnetLink) ? null : new Uri(magnetLink);
            return magnet;
        }

        public static List<string> ExtractGenres(this IElement row)
        {
            var genres = new List<string>();
            row.ExtractFromRow(
                "span:contains(\"Gênero:\")", genreText =>
                {
                    ExtractPattern(
                        genreText, @"Gênero:\s*(.+)", genre =>
                        {
                            genres = genre.Split('|').Select(token => token.Trim()).ToList();
                        });
                });
            return genres;
        }

        public static List<int> ExtractCategory(this IElement row)
        {
            var releaseCategory = new List<int>();
            row.ExtractFromRow(
                "div.title > a", categoryText =>
                {
                    var hasSeasonInfo = categoryText.IndexOf("temporada", StringComparison.OrdinalIgnoreCase) >= 0;
                    releaseCategory.Add(hasSeasonInfo ? TorznabCatType.TV.ID : TorznabCatType.Movies.ID);
                });
            return releaseCategory;
        }

        public static DateTime ExtractReleaseDate(this IElement row)
        {
            var result = DateTime.MinValue;
            row.ExtractFromRow(
                "span:contains(\"Lançamento:\")", releaseDateText =>
                {
                    ExtractPattern(
                        releaseDateText, @"Lançamento:\s*(.+)", releaseDate =>
                        {
                            DateTime.TryParseExact(
                                releaseDate, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
                        });
                });
            return result;
        }

        public static List<string> ExtractSubtitles(this IElement row)
        {
            var subtitles = new List<string>();
            row.ExtractFromRow(
                "span:contains(\"Legenda:\")", subtitleText =>
                {
                    ExtractPattern(
                        subtitleText, @"Legenda:\s*(.+)", subtitle =>
                        {
                            subtitles.Add(subtitle);
                        });
                });
            return subtitles;
        }

        public static long? ExtractSize(this IElement row)
        {
            long? result = null;
            row.ExtractFromRow(
                "span:contains(\"Tamanho:\")", sizeText =>
                {
                    ExtractPattern(
                        sizeText, @"Tamanho:\s*(.+)", size =>
                        {
                            result = ParseUtil.GetBytes(size);
                        });
                });
            return result;
        }

        public static List<string> ExtractLanguages(this IElement row)
        {
            var languages = new List<string>();
            row.ExtractFromRow(
                "span:contains(\"Áudio:\")", audioText =>
                {
                    ExtractPattern(
                        audioText, @"Áudio:\s*(.+)", audio =>
                        {
                            languages = audio.Split('|').Select(token => token.Trim()).ToList();
                        });
                });
            return languages;
        }

        public static void ExtractFromRow(this IElement row, string selector, Action<string> extraction)
        {
            var element = row.QuerySelector(selector);
            if (element != null)
            {
                extraction(element.TextContent);
            }
        }

        public static void ExtractPattern(string text, string pattern, Action<string> extraction)
        {
            var match = Regex.Match(text, pattern);
            if (match.Success)
            {
                extraction(match.Groups[1].Value.Trim());
            }
        }
    }
    public abstract class PublicBrazilianParser : IParseIndexerResponse
    {
        protected string _name;

        protected PublicBrazilianParser(string name)
        {
            _name = name;
        }

        public abstract IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse);

        public string ExtractTitleOrDefault(IElement downloadButton, string title)
        {
            var description = GetTitleElementOrNull(downloadButton);
            if (description != null)
            {
                var descriptionText = description.TextContent;
                RowParsingExtensions.ExtractPattern(
                    descriptionText, @"\b(\d{3,4}p)\b", resolution =>
                    {
                        title = $"[{_name}] " + CleanTitle(title) + $" {resolution}";
                    });
            }

            return title;
        }

        protected static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            // Remove size info in parentheses
            title = Regex.Replace(title, @"\(\d+(?:\.\d+)?\s*(?:GB|MB)\)", "", RegexOptions.IgnoreCase);

            // Remove quality info
            title = Regex.Replace(title, @"\b(?:720p|1080p|2160p|4K)\b", "", RegexOptions.IgnoreCase);

            // Remove source info
            title = Regex.Replace(title, @"\b(?:WEB-DL|BRRip|HDRip|WEBRip|BluRay|Torrent)\b", "", RegexOptions.IgnoreCase);

            // Remove brackets/parentheses content
            title = Regex.Replace(title, @"\[(?:.*?)\]|\((?:.*?)\)", "", RegexOptions.IgnoreCase);

            // Remove dangling punctuation and separators
            title = Regex.Replace(title, @"[\\/,|~_-]+\s*|\s*[\\/,|~_-]+", " ", RegexOptions.IgnoreCase);

            // Clean up multiple spaces
            title = Regex.Replace(title, @"\s+", " ");

            // Remove dots between words but keep dots in version numbers
            title = Regex.Replace(title, @"(?<!\d)\.(?!\d)", " ", RegexOptions.IgnoreCase);

            // Remove any remaining punctuation at start/end
            title = title.Trim(' ', '.', ',', '-', '_', '~', '/', '\\', '|');
            return title;
        }

        protected abstract INode GetTitleElementOrNull(IElement downloadButton);

        protected static bool NotSpanTag(INode description) =>
            (description.NodeType != NodeType.Element || ((Element)description).TagName != "SPAN");
    }
}
