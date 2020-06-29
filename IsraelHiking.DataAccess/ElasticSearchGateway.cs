﻿using Elasticsearch.Net;
using IsraelHiking.Common;
using IsraelHiking.Common.Configuration;
using IsraelHiking.Common.Extensions;
using IsraelHiking.DataAccessInterfaces;
using IsraelHiking.DataAccessInterfaces.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nest;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Feature = NetTopologySuite.Features.Feature;

namespace IsraelHiking.DataAccess
{
    /// <summary>
	/// A JSON serializer that uses Json.NET for serialization and able to parse geojson objects
	/// </summary>
	public class GeoJsonNetSerializer : IElasticsearchSerializer
    {
        private static readonly Encoding ExpectedEncoding = new UTF8Encoding(false);

        private readonly ConcurrentDictionary<string, IPropertyMapping> Properties = new ConcurrentDictionary<string, IPropertyMapping>();
        private readonly JsonSerializerSettings _noneIndentedSettings;
        private readonly JsonSerializerSettings _indentedSettings;

        public GeoJsonNetSerializer(IConnectionSettingsValues settings)
        {
            _noneIndentedSettings = CreateSettings(SerializationFormatting.None, settings);
            _indentedSettings = CreateSettings(SerializationFormatting.Indented, settings);
        }

        public IPropertyMapping CreatePropertyMapping(MemberInfo memberInfo)
        {
            var memberInfoString = $"{memberInfo.DeclaringType?.FullName}.{memberInfo.Name}";
            if (Properties.TryGetValue(memberInfoString, out var mapping))
                return mapping;

            mapping = PropertyMappingFromAttributes(memberInfo);
            Properties.TryAdd(memberInfoString, mapping);
            return mapping;
        }

        public T Deserialize<T>(Stream stream)
        {
            if (stream == null) return default;

            using var streamReader = new StreamReader(stream);
            using var jsonTextReader = new JsonTextReader(streamReader);
            return JsonSerializer.Create(_noneIndentedSettings).Deserialize<T>(jsonTextReader);
        }

        public Task<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
        {
            //Json.NET does not support reading a stream asynchronously :(
            var result = Deserialize<T>(stream);
            return Task.FromResult(result);
        }

        public void Serialize(object data, Stream writableStream, SerializationFormatting formatting = SerializationFormatting.Indented)
        {
            var serializer = formatting == SerializationFormatting.Indented
                ? JsonSerializer.Create(_indentedSettings)
                : JsonSerializer.Create(_noneIndentedSettings);

            using var writer = new StreamWriter(writableStream, ExpectedEncoding, 1024, true);
            using var jsonWriter = new JsonTextWriter(writer);
            serializer.Serialize(jsonWriter, data);
            writer.Flush();
            jsonWriter.Flush();
        }

        private static IPropertyMapping PropertyMappingFromAttributes(MemberInfo memberInfo)
        {
            var jsonProperty = memberInfo.GetCustomAttribute<JsonPropertyAttribute>(true);
            var dataMember = memberInfo.GetCustomAttribute<DataMemberAttribute>(true);
            var ignoreProperty = memberInfo.GetCustomAttribute<JsonIgnoreAttribute>(true);
            if (jsonProperty == null && ignoreProperty == null && dataMember == null) return null;

            return new PropertyMapping { Name = jsonProperty?.PropertyName ?? dataMember?.Name, Ignore = ignoreProperty != null };
        }

        private JsonSerializerSettings CreateSettings(SerializationFormatting formatting, IConnectionSettingsValues connectionSettings)
        {
            var settings = new JsonSerializerSettings()
            {
                Formatting = formatting == SerializationFormatting.Indented ? Formatting.Indented : Formatting.None,
                ContractResolver = new ElasticContractResolver(connectionSettings, null),
                DefaultValueHandling = DefaultValueHandling.Include,
                NullValueHandling = NullValueHandling.Ignore
            };
            foreach(var converter in GeoJsonSerializer.Create(GeometryFactory.Default, 3).Converters)
            {
                settings.Converters.Add(converter);
            }
            return settings;
        }
    }

    public class ElasticSearchGateway :
        IInitializable,
        IPointsOfInterestRepository,
        IHighwaysRepository,
        ISearchRepository,
        IUserLayersRepository,
        IImagesRepository,
        IExternalSourcesRepository,
        IShareUrlsRepository
    {
        private const int PAGE_SIZE = 10000;

        private const string PROPERTIES = "properties";
        private const string OSM_POIS_INDEX1 = "osm_names1";
        private const string OSM_POIS_INDEX2 = "osm_names2";
        private const string OSM_POIS_ALIAS = "osm_names";
        private const string OSM_HIGHWAYS_INDEX1 = "osm_highways1";
        private const string OSM_HIGHWAYS_INDEX2 = "osm_highways2";
        private const string OSM_HIGHWAYS_ALIAS = "osm_highways";
        private const string SHARES = "shares";
        private const string CUSTOM_USER_LAYERS = "custom_user_layers";
        private const string EXTERNAL_POIS = "external_pois";
        private const string IMAGES = "images";
        private const string REBUILD_LOG = "rebuild_log";

        private const int NUMBER_OF_RESULTS = 10;
        private readonly ILogger _logger;
        private readonly ConfigurationData _options;
        private IElasticClient _elasticClient;

        public ElasticSearchGateway(IOptions<ConfigurationData> options, ILogger logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public Task Initialize()
        {
            return Task.Run(() =>
            {
                var uri = _options.ElasticsearchServerAddress;
                var pool = new SingleNodeConnectionPool(new Uri(uri));
                var connectionString = new ConnectionSettings(
                    pool,
                    new HttpConnection(),
                    new SerializerFactory(s => new GeoJsonNetSerializer(s)))
                    .PrettyJson();
                _elasticClient = new ElasticClient(connectionString);
                if (_elasticClient.IndexExists(OSM_POIS_INDEX1).Exists == false &&
                    _elasticClient.IndexExists(OSM_POIS_INDEX2).Exists == false)
                {
                    CreatePointsOfInterestIndex(OSM_POIS_INDEX1);
                    _elasticClient.Alias(a => a.Add(add => add.Alias(OSM_POIS_ALIAS).Index(OSM_POIS_INDEX1)));
                }
                if (_elasticClient.IndexExists(OSM_HIGHWAYS_INDEX1).Exists == false &&
                    _elasticClient.IndexExists(OSM_HIGHWAYS_INDEX2).Exists == false)
                {
                    CreateHighwaysIndex(OSM_HIGHWAYS_INDEX1);
                    _elasticClient.Alias(a => a.Add(add => add.Alias(OSM_HIGHWAYS_ALIAS).Index(OSM_HIGHWAYS_INDEX1)));
                }
                if (_elasticClient.IndexExists(SHARES).Exists == false)
                {
                    _elasticClient.CreateIndex(SHARES);
                }
                if (_elasticClient.IndexExists(CUSTOM_USER_LAYERS).Exists == false)
                {
                    _elasticClient.CreateIndex(CUSTOM_USER_LAYERS);
                }
                if (_elasticClient.IndexExists(IMAGES).Exists == false)
                {
                    CreateImagesIndex();
                }
                _logger.LogInformation("Finished initialing elasticsearch with uri: " + uri);
            });
        }

        private List<T> GetAllItemsByScrolling<T>(ISearchResponse<T> response) where T: class
        {
            var list = new List<T>();
            list.AddRange(response.Documents.ToList());
            var results = _elasticClient.Scroll<T>("10s", response.ScrollId);
            list.AddRange(results.Documents.ToList());
            while (results.Documents.Any())
            {
                results = _elasticClient.Scroll<T>("10s", results.ScrollId);
                list.AddRange(results.Documents.ToList());
            }
            return list;
        }

        private QueryContainer FeatureNameSearchQueryWithFactor(QueryContainerDescriptor<Feature> q, string searchTerm, string language)
        {
            return q.FunctionScore(
                fs => fs.Query(
                    qi => FeatureNameSearchQuery(qi, searchTerm, language)
                ).Functions(fn => fn.FieldValueFactor(f => f.Field($"{PROPERTIES}.{FeatureAttributes.POI_SEARCH_FACTOR}")))
            );
        }

        private QueryContainer FeatureNameSearchQuery(QueryContainerDescriptor<Feature> q, string searchTerm, string language)
        {
            return q.DisMax(
                dm => dm.Queries(
                    dmq => dmq.Match(
                        mm => mm.Query(searchTerm)
                            .Field($"{PROPERTIES}.{FeatureAttributes.POI_NAMES}.{Languages.ALL}")
                            .Fuzziness(Fuzziness.Auto)
                    ),
                    dmq => dmq.Match(
                        m => m.Query(searchTerm)
                            .Boost(1.2)
                            .Field($"{PROPERTIES}.{FeatureAttributes.POI_NAMES}.{language}")
                            .Fuzziness(Fuzziness.Auto)
                    )
                )
            );
        }

        public async Task<List<Feature>> Search(string searchTerm, string language)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return new List<Feature>();
            }
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_POIS_ALIAS)
                    .Size(NUMBER_OF_RESULTS)
                    .TrackScores()
                    .Sort(f => f.Descending("_score"))
                    .Query(q => FeatureNameSearchQueryWithFactor(q, searchTerm, language))
            );
            return response.Documents.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
        }

        public async Task<List<Feature>> SearchPlaces(string place, string language)
        {
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_POIS_ALIAS)
                    .Size(5)
                    .TrackScores()
                    .Sort(f => f.Descending("_score"))
                    .Query(q => FeatureNameSearchQuery(q, place, language)
                                && q.Term(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_CONTAINER}").Value(true))
                    )
            );
            return response.Documents.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
        }

        public async Task<List<Feature>> SearchByLocation(Coordinate nortEast, Coordinate southWest, string searchTerm, string language)
        {
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_POIS_ALIAS)
                    .Size(NUMBER_OF_RESULTS)
                    .TrackScores()
                    .Sort(f => f.Descending("_score"))
                    .Query(
                        q => FeatureNameSearchQueryWithFactor(q, searchTerm, language) &&
                             q.GeoBoundingBox(b => ConvertToGeoBoundingBox(b, nortEast, southWest))
                    )
            );
            return response.Documents.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
        }

        public async Task<List<Feature>> GetContainers(Coordinate coordinate)
        {
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_POIS_ALIAS)
                    .Size(100)
                    .Query(q =>
                        q.GeoShapePoint(g =>
                            g.Coordinates(ConvertCoordinate(coordinate))
                                .Field(f => f.Geometry)
                                .Relation(GeoShapeRelation.Contains))
                        && q.Term(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_CONTAINER}").Value(true)))
            );
            return response.Documents.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
        }

        private (string currentIndex, string newIndex) GetIndicesStatus(string index1, string index2, string alias)
        {
            var currentIndex = index1;
            var newIndex = index2;
            if (_elasticClient.GetIndicesPointingToAlias(alias).Contains(index2))
            {
                currentIndex = index2;
                newIndex = index1;
            }
            return (currentIndex, newIndex);
        }

        private async Task SwitchIndices(string currentIndex, string newIndex, string alias)
        {
            await _elasticClient.AliasAsync(a => a
                .Remove(i => i.Alias(alias).Index(currentIndex))
                .Add(i => i.Alias(alias).Index(newIndex))
            );
            await _elasticClient.DeleteIndexAsync(currentIndex);
        }

        public async Task UpdateHighwaysZeroDownTime(List<Feature> highways)
        {
            var (currentIndex, newIndex) = GetIndicesStatus(OSM_HIGHWAYS_INDEX1, OSM_HIGHWAYS_INDEX2, OSM_HIGHWAYS_ALIAS);

            await CreateHighwaysIndex(newIndex);
            await UpdateUsingPaging(highways, newIndex);

            await SwitchIndices(currentIndex, newIndex, OSM_HIGHWAYS_ALIAS);
        }

        public async Task DeleteHighwaysById(string id)
        {
            var fullId = GeoJsonExtensions.GetId(Sources.OSM, id);
            await _elasticClient.DeleteAsync<Feature>(fullId, d => d.Index(OSM_HIGHWAYS_ALIAS));
        }

        public async Task StorePointsOfInterestDataToSecondaryIndex(List<Feature> pointsOfInterest)
        {
            var (_, newIndex) = GetIndicesStatus(OSM_POIS_INDEX1, OSM_POIS_INDEX2, OSM_POIS_ALIAS);
            await CreatePointsOfInterestIndex(newIndex);
            await UpdateUsingPaging(pointsOfInterest, newIndex);
        }

        public async Task SwitchPointsOfInterestIndices()
        {
            var (currentIndex, newIndex) = GetIndicesStatus(OSM_POIS_INDEX1, OSM_POIS_INDEX2, OSM_POIS_ALIAS);
            await SwitchIndices(currentIndex, newIndex, OSM_POIS_ALIAS);
        }

        public Task UpdateHighwaysData(List<Feature> features)
        {
            return UpdateData(features, OSM_HIGHWAYS_ALIAS);
        }

        public Task UpdatePointsOfInterestData(List<Feature> features)
        {
            return UpdateData(features, OSM_POIS_ALIAS);
        }

        private async Task UpdateData(List<Feature> features, string alias)
        {
            var result = await _elasticClient.BulkAsync(bulk =>
            {
                foreach (var feature in features)
                {
                    bulk.Index<Feature>(i => i.Index(alias).Document(feature).Id(feature.GetId()));
                }
                return bulk;
            });
            if (result.IsValid == false)
            {
                result.ItemsWithErrors.ToList().ForEach(i => _logger.LogError($"Inserting {i.Id} failed with error: {i.Error?.Reason ?? string.Empty} caused by: {i.Error?.CausedBy?.Reason ?? string.Empty}"));
            }
        }

        public async Task<List<Feature>> GetHighways(Coordinate northEast, Coordinate southWest)
        {
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_HIGHWAYS_ALIAS)
                    .Size(5000)
                    .Query(
                        q => q.GeoShapeEnvelope(
                            e => e.Coordinates(new[]
                                {
                                    ConvertCoordinate(northEast),
                                    ConvertCoordinate(southWest),
                                }).Field(f => f.Geometry)
                                .Relation(GeoShapeRelation.Intersects)
                        )
                    )
            );
            var json = response.DebugInformation;
            return response.Documents.ToList();
        }

        private GeoCoordinate ConvertCoordinate(Coordinate coordinate)
        {
            return new GeoCoordinate(coordinate.Y, coordinate.X);
        }

        public async Task<List<Feature>> GetPointsOfInterest(Coordinate northEast, Coordinate southWest, string[] categories, string language)
        {
            var languages = language == Languages.ALL ? Languages.Array : new[] { language };
            languages = languages.Concat(new[] { Languages.ALL }).ToArray();
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(OSM_POIS_ALIAS)
                    .Size(10000).Query(
                        q => q.GeoBoundingBox(
                            b => ConvertToGeoBoundingBox(b, northEast, southWest)
                        ) &&
                        q.Terms(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_CATEGORY}").Terms(categories.Select(c => c.ToLower()).ToArray())) &&
                        q.Terms(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_LANGUAGE}").Terms(languages))
                    )
            );
            return response.Documents.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
        }

        public async Task<List<Feature>> GetAllPointsOfInterest(bool withDeleted)
        {
            _elasticClient.Refresh(OSM_POIS_ALIAS);
            var categories = Categories.Points.Concat(Categories.Routes).Select(c => c.ToLower()).ToArray();
            var response = await _elasticClient.SearchAsync<Feature>(s => s.Index(OSM_POIS_ALIAS)
                    .Size(10000)
                    .Scroll("10s")
                    .Query(q => q.Terms(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_CATEGORY}").Terms(categories))
                    ));
            var list = GetAllItemsByScrolling(response);
            if (withDeleted == false)
            {
                list = list.Where(f => !f.Attributes.Exists(FeatureAttributes.POI_DELETED)).ToList();
            }
            return list;
        }

        private GeoBoundingBoxQueryDescriptor<Feature> ConvertToGeoBoundingBox(GeoBoundingBoxQueryDescriptor<Feature> b,
            Coordinate northEast, Coordinate southWest)
        {
            return b.BoundingBox(
                bb => bb.TopLeft(new GeoCoordinate(northEast.Y, southWest.X))
                    .BottomRight(new GeoCoordinate(southWest.Y, northEast.X))
            ).Field($"{PROPERTIES}.{FeatureAttributes.POI_GEOLOCATION}");
        }

        public async Task<List<Feature>> GetPointsOfInterestUpdates(DateTime lastModifiedDate)
        {
            var list = new List<Feature>();
            var categories = Categories.Points.Concat(Categories.Routes).Select(c => c.ToLower()).ToArray();
            var response = await _elasticClient.SearchAsync<Feature>(s => s.Index(OSM_POIS_ALIAS)
                    .Size(10000)
                    .Scroll("10s")
                    .Query(q => q.DateRange(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_LAST_MODIFIED}").GreaterThan(lastModifiedDate))
                        && q.Terms(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_CATEGORY}").Terms(categories))
                    ));
            return GetAllItemsByScrolling(response);
        }

        public async Task<Feature> GetPointOfInterestById(string id, string source)
        {
            var fullId = GeoJsonExtensions.GetId(source, id);
            var response = await _elasticClient.GetAsync<Feature>(fullId, r => r.Index(OSM_POIS_ALIAS));
            return response.Source;
        }

        public async Task DeleteOsmPointOfInterestById(string id, DateTime? timeStamp)
        {
            var feature = await GetPointOfInterestById(id, Sources.OSM);
            if (feature != null)
            {
                feature.Attributes.AddOrUpdate(FeatureAttributes.POI_DELETED, true);
                feature.Attributes.AddOrUpdate(FeatureAttributes.POI_LAST_MODIFIED, (timeStamp ?? DateTime.Now).ToString("o"));
                await UpdatePointsOfInterestData(new List<Feature> { feature });
            }
        }

        public Task DeletePointOfInterestById(string id, string source)
        {
            var fullId = GeoJsonExtensions.GetId(source, id);
            return _elasticClient.DeleteAsync<Feature>(fullId, d => d.Index(OSM_POIS_ALIAS));
        }

        public async Task<List<Feature>> GetExternalPoisBySource(string source)
        {
            var response = await _elasticClient.SearchAsync<Feature>(
                s => s.Index(EXTERNAL_POIS)
                    .Size(10000)
                    .Scroll("10m")
                    .Query(q =>
                        q.Term(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_SOURCE}").Value(source.ToLower()))
                    )
            );
            var features = GetAllItemsByScrolling(response);
            _logger.LogInformation($"Got {features.Count} features for source {source}");
            return features;
        }

        public async Task<Feature> GetExternalPoiById(string id, string source)
        {
            var response = await _elasticClient.GetAsync<Feature>(GeoJsonExtensions.GetId(source, id), r => r.Index(EXTERNAL_POIS));
            return response.Source;
        }

        public async Task AddExternalPois(List<Feature> features)
        {
            if (_elasticClient.IndexExists(EXTERNAL_POIS).Exists == false)
            {
                await CreateExternalPoisIndex();
            }
            await UpdateData(features, EXTERNAL_POIS);
        }

        public Task DeleteExternalPoisBySource(string source)
        {
            return _elasticClient.DeleteByQueryAsync<Feature>(d =>
                d.Index(EXTERNAL_POIS)
                .Query(q =>
                    q.Term(t => t.Field($"{PROPERTIES}.{FeatureAttributes.POI_SOURCE}").Value(source.ToLower()))
                )
            );
        }

        private Task CreateHighwaysIndex(string highwaysIndexName)
        {
            return _elasticClient.CreateIndexAsync(highwaysIndexName,
                c => c.Mappings(ms =>
                    ms.Map<Feature>(m =>
                        m.Properties(ps =>
                            ps.GeoShape(g =>
                                g.Name(f => f.Geometry)
                                .Tree(GeoTree.Geohash)
                                .TreeLevels(10)
                                .DistanceErrorPercentage(0.2)
                            )
                        )
                    )
                )
            );
        }

        private Task CreatePointsOfInterestIndex(string poisIndexName)
        {
            return _elasticClient.CreateIndexAsync(poisIndexName,
                c => c.Mappings(ms =>
                    ms.Map<Feature>(m =>
                        m.Properties(ps =>
                            ps.Object<AttributesTable>(o => o
                                .Name(PROPERTIES)
                                .Properties(p => p.GeoPoint(s => s.Name(FeatureAttributes.POI_GEOLOCATION)))
                                .Properties(p => p.Keyword(s => s.Name(FeatureAttributes.ID)))
                            ).GeoShape(g =>
                                g.Name(f => f.Geometry)
                                .Tree(GeoTree.Geohash)
                                .TreeLevels(10)
                                .DistanceErrorPercentage(0.2)
                            )
                        )
                    )
                ).Settings(s => s.Setting("index.mapping.total_fields.limit", 10000))
            );
        }

        private Task CreateExternalPoisIndex()
        {
            return _elasticClient.CreateIndexAsync(EXTERNAL_POIS,
                c => c.Mappings(ms =>
                    ms.Map<Feature>(m =>
                        m.Properties(fp =>
                            fp.Object<AttributesTable>(a => a
                                .Name(PROPERTIES)
                                .Properties(p => p.Keyword(s => s.Name(FeatureAttributes.ID)))
                            )
                        )
                    )
                ).Settings(s => s.Setting("index.mapping.total_fields.limit", 10000))
            );
        }

        private Task CreateImagesIndex()
        {
            return _elasticClient.CreateIndexAsync(IMAGES, c =>
                c.Mappings(ms =>
                    ms.Map<ImageItem>(m =>
                        m.Properties(p =>
                            p.Keyword(k => k.Name(ii => ii.Hash))
                             .Keyword(s => s.Name(n => n.ImageUrls))
                             .Binary(a => a.Name(i => i.Thumbnail))
                        )
                    )
                )
            );
        }

        private async Task UpdateUsingPaging(List<Feature> features, string alias)
        {
            _logger.LogInformation($"Starting indexing {features.Count} records");
            var smallCahceList = new List<Feature>(PAGE_SIZE);
            int total = 0;
            foreach (var feature in features)
            {
                smallCahceList.Add(feature);
                if (smallCahceList.Count < PAGE_SIZE)
                {
                    continue;
                }
                total += smallCahceList.Count;
                await UpdateData(smallCahceList, alias);
                _logger.LogInformation($"Indexed {total} records of {features.Count}");
                smallCahceList.Clear();
            }
            await UpdateData(smallCahceList, alias);
            _logger.LogInformation($"Finished indexing {features.Count} records");
        }

        public Task<List<ShareUrl>> GetUrls()
        {
            //The thing to know about scrollTimeout is that it resets after each call to the scroll so it only needs to be big enough to stay alive between calls.
            //when it expires, elastic will delete the entire scroll.
            _logger.LogInformation("Starting to get all shares");
            ISearchResponse<ShareUrl> initialResponse = _elasticClient.Search<ShareUrl>(s => s
                .Index(SHARES)
                .From(0)
                .Size(1000)
                .MatchAll()
                .Scroll("10m"));
            List<ShareUrl> results = new List<ShareUrl>();
            if (!initialResponse.IsValid || string.IsNullOrEmpty(initialResponse.ScrollId))
            {
                throw new Exception(initialResponse.ServerError?.Error?.Reason ?? "Unable to get urls");
            }

            if (initialResponse.Documents.Any())
                results.AddRange(initialResponse.Documents);
            string scrollid = initialResponse.ScrollId;
            bool isScrollSetHasData = true;
            int page = 0;
            while (isScrollSetHasData)
            {
                page++;
                _logger.LogInformation($"More data needs to be fetched, page: {page}");
                ISearchResponse<ShareUrl> loopingResponse = _elasticClient.Scroll<ShareUrl>("10m", scrollid);
                if (loopingResponse.IsValid)
                {
                    results.AddRange(loopingResponse.Documents);
                    scrollid = loopingResponse.ScrollId;
                }

                isScrollSetHasData = loopingResponse.Documents.Any();
            }

            //This would be garbage collected on it's own after scrollTimeout expired from it's last call but we'll clean up our room when we're done per best practice.
            _elasticClient.ClearScroll(new ClearScrollRequest(scrollid));
            _logger.LogInformation("Finished getting all shares: " + results.Count);
            return Task.FromResult(results);
        }

        public Task AddUrl(ShareUrl shareUrl)
        {
            return _elasticClient.IndexAsync(shareUrl, r => r.Index(SHARES).Id(shareUrl.Id));
        }

        public async Task<ShareUrl> GetUrlById(string id)
        {
            var response = await _elasticClient.GetAsync<ShareUrl>(id, r => r.Index(SHARES));
            return response.Source;
        }

        public async Task<List<ShareUrl>> GetUrlsByUser(string osmUserId)
        {
            var response = await _elasticClient.SearchAsync<ShareUrl>(s => s.Index(SHARES).Size(1000)
                .Query(q => q.Term(t => t.OsmUserId, osmUserId)));
            return response.Documents.ToList();

        }

        public Task Delete(ShareUrl shareUrl)
        {
            return _elasticClient.DeleteAsync<ShareUrl>(shareUrl.Id, d => d.Index(SHARES));
        }

        public Task Update(ShareUrl shareUrl)
        {
            return AddUrl(shareUrl);
        }

        public async Task<List<MapLayerData>> GetUserLayers(string osmUserId)
        {
            var response = await _elasticClient.SearchAsync<MapLayerData>(s => s.Index(CUSTOM_USER_LAYERS).Size(1000)
                .Query(q => q.Term(t => t.OsmUserId, osmUserId)));
            var layers = response.Documents.ToList();
            return response.Hits.Select((h, i) =>
            {
                layers[i].Id = h.Id;
                return layers[i];
            }).ToList();
        }

        public async Task<MapLayerData> GetUserLayerById(string id)
        {
            var response = await _elasticClient.GetAsync<MapLayerData>(id, r => r.Index(CUSTOM_USER_LAYERS));
            response.Source.Id = id;
            return response.Source;
        }

        public async Task<MapLayerData> AddUserLayer(MapLayerData layerData)
        {
            var response = await _elasticClient.IndexAsync(layerData, r => r.Index(CUSTOM_USER_LAYERS));
            layerData.Id = response.Id;
            return layerData;
        }

        public Task UpdateUserLayer(MapLayerData layerData)
        {
            return _elasticClient.IndexAsync(layerData, r => r.Index(CUSTOM_USER_LAYERS).Id(layerData.Id));
        }

        public Task DeleteUserLayer(MapLayerData layerData)
        {
            return _elasticClient.DeleteAsync<MapLayerData>(layerData.Id, d => d.Index(CUSTOM_USER_LAYERS));
        }

        public async Task<ImageItem> GetImageByUrl(string url)
        {
            var response = await _elasticClient.SearchAsync<ImageItem>(s =>
                s.Index(IMAGES)
                .Query(q => q.Match(m => m.Field(i => i.ImageUrls).Query(url)))
            );
            return response.Documents.FirstOrDefault();
        }

        public async Task<ImageItem> GetImageByHash(string hash)
        {
            var response = await _elasticClient.GetAsync<ImageItem>(hash, r => r.Index(IMAGES));
            return response.Source;
        }
        public async Task<List<string>> GetAllUrls()
        {
            _elasticClient.Refresh(IMAGES);
            var response = await _elasticClient.SearchAsync<ImageItem>(
                s => s.Index(IMAGES)
                    .Size(10000)
                    .Scroll("10s")
                    .Source(sf => sf
                    .Includes(i => i.Fields(f => f.ImageUrls, f => f.Hash))
                ).Query(q => q.MatchAll())
            );
            var list = GetAllItemsByScrolling(response);
            return list.SelectMany(i => i.ImageUrls).ToList();
        }

        public Task StoreImage(ImageItem imageItem)
        {
            return _elasticClient.IndexAsync(imageItem, r => r.Index(IMAGES).Id(imageItem.Hash));
        }

        public async Task DeleteImageByUrl(string url)
        {
            var imageItem = await GetImageByUrl(url);
            if (imageItem != null)
            {
                await _elasticClient.DeleteAsync<Feature>(imageItem.Hash, d => d.Index(IMAGES));
            }
        }

        public Task StoreRebuildContext(RebuildContext context)
        {
            return _elasticClient.IndexAsync(context, r => r.Index(REBUILD_LOG).Id(context.StartTime.ToString("o")));
        }

        public async Task<DateTime> GetLastSuccessfulRebuildTime()
        {
            const string MAX_DATE = "max_date";
            var response = await _elasticClient.SearchAsync<RebuildContext>(s => s.Index(REBUILD_LOG)
                    .Size(1)
                    .Query(q => q.Term(t => t.Field(r => r.Succeeded).Value(true)))
                    .Aggregations(a => a.Max(MAX_DATE, m => m.Field(r => r.StartTime)))
                    );
            if (DateTime.TryParse(response.Aggs.Max(MAX_DATE).ValueAsString, out var date))
            {
                return date;
            }
            return DateTime.MinValue;
        }
    }
}
