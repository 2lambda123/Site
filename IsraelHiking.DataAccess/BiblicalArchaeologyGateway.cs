using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IsraelHiking.Common;
using IsraelHiking.Common.Extensions;
using IsraelHiking.DataAccessInterfaces;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Newtonsoft.Json;

namespace IsraelHiking.DataAccess
{
    internal class JsonBiblicalArchaeologyItem
    {
        [JsonProperty("id")]
        public long Id { get; set; }
        [JsonProperty("start")]
        public LatLng Start { get; set; }
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("last_modified")]
        public DateTime LastModified { get; set; }
    }

    internal class JsonBiblicalArchaeologyItemExtended : JsonBiblicalArchaeologyItem
    {
        [JsonProperty("picture")]
        public string Picture { get; set; }
        [JsonProperty("link")]
        public string Link { get; set; }
        [JsonProperty("prolog")]
        public string Prolog { get; set; }
    }

    public class BiblicalArchaeologyGateway : IBiblicalArchaeologyGateway
    {
        private const string BASE_API_ADDRESS = "https://biblical-archaeology.org/wp-json/map/v1/";

        private const string LOGO =
            "https://user-images.githubusercontent.com/3269297/222973497-4cb1719d-8d1e-46ba-8152-06962c9f57e5.png";
        
        private readonly IHttpClientFactory _httpClientFactory;

        public BiblicalArchaeologyGateway(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<List<Feature>> GetAll()
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{BASE_API_ADDRESS}locations");
            var content = await response.Content.ReadAsStringAsync();
            var item = JsonConvert.DeserializeObject<List<JsonBiblicalArchaeologyItem>>(content);
            return item.Select(ConvertToPointFeature).ToList();
        }

        public async Task<Feature> GetById(string id)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{BASE_API_ADDRESS}location-date/{id}");
            var content = await response.Content.ReadAsStringAsync();
            var item = JsonConvert.DeserializeObject<JsonBiblicalArchaeologyItemExtended>(content);
            var attributes = GetAttributes(item);
            var description = item.Prolog ?? string.Empty;
            attributes.Add(FeatureAttributes.DESCRIPTION + ":" + Languages.HEBREW, description);
            if (!string.IsNullOrWhiteSpace(item.Picture))
            {
                attributes.Add(FeatureAttributes.IMAGE_URL, item.Picture);
            }
            attributes.Add(FeatureAttributes.WEBSITE, item.Link);
            attributes.Add(FeatureAttributes.POI_SOURCE_IMAGE_URL, LOGO);
            
            var point = new Point(item.Start.ToCoordinate());
            var feature = new Feature(point, GetAttributes(item));
            feature.SetTitles();
            feature.SetId();
            return feature;
        }
        
        private Feature ConvertToPointFeature(JsonBiblicalArchaeologyItem item)
        {
            var point = new Point(item.Start.ToCoordinate());
            return new Feature(point, GetAttributes(item));
        }

        private AttributesTable GetAttributes(JsonBiblicalArchaeologyItem item)
        {
            var attributes = new AttributesTable
            {
                {FeatureAttributes.ID, item.Id.ToString()},
                {FeatureAttributes.NAME, item.Title},
                {FeatureAttributes.NAME + ":" + Languages.HEBREW, item.Title},
                {FeatureAttributes.POI_SOURCE, Sources.BIBLICAL_ARCHAEOLOGY},
                {FeatureAttributes.POI_CATEGORY, Categories.HISTORIC},
                {FeatureAttributes.POI_LANGUAGE, Languages.HEBREW},
                {FeatureAttributes.POI_ICON, "icon-ruins"},
                {FeatureAttributes.POI_ICON_COLOR, "black"},
                {FeatureAttributes.POI_SEARCH_FACTOR, 1.0}
            };
            attributes.SetLastModified(item.LastModified);
            attributes.SetLocation(new Coordinate(item.Start.Lng, item.Start.Lat));
            return attributes;
        }
    }
}