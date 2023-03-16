using System.Collections.Generic;
using System.Threading.Tasks;
using NetTopologySuite.Features;

namespace IsraelHiking.DataAccessInterfaces
{
    public interface IBiblicalArchaeologyGateway
    {
        Task<List<Feature>> GetAll();
        Task<Feature> GetById(string id);
    }
}