using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IsraelHiking.Common;
using IsraelHiking.Common.Extensions;
using IsraelHiking.DataAccessInterfaces;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;

namespace IsraelHiking.API.Services.Poi
{
    /// <summary>
    /// Adapts from biblical-archaeology interface to business logic point of interest
    /// </summary>
    public class BiblicalArchaeologyPointsOfInterestAdapter : IPointsOfInterestAdapter
    {
        /// <inheritdoc />
        public string Source => Sources.BIBLICAL_ARCHAEOLOGY;
        
        private readonly IBiblicalArchaeologyGateway _gateway;
        private readonly ILogger _logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="gateway"></param>
        /// <param name="logger"></param>
        public BiblicalArchaeologyPointsOfInterestAdapter(IBiblicalArchaeologyGateway gateway, ILogger logger)
        {
            _gateway = gateway;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<List<Feature>> GetAll()
        {
            _logger.LogInformation("Getting data from biblical-archaeology.");
            var slimFeatures = await _gateway.GetAll();
            var features = new List<Feature>();
            foreach (var slimFeature in slimFeatures)
            {
                features.Add(await _gateway.GetById(slimFeature.Attributes[FeatureAttributes.ID].ToString()));
            }
            _logger.LogInformation($"Got {features.Count} points from biblical-archaeology.");
            return features;
        }

        /// <inheritdoc />
        public async Task<List<Feature>> GetUpdates(DateTime lastModifiedDate)
        {
            var slimFeatures = await _gateway.GetAll();
            var features = new List<Feature>();
            foreach (var slimFeature in slimFeatures.Where(f => f.GetLastModified() > lastModifiedDate))
            {
                features.Add(await _gateway.GetById(slimFeature.Attributes[FeatureAttributes.ID].ToString()));
            }
            return features;
        }
    }
}