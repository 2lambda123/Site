using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace IsraelHiking.DataAccess.Tests
{
    [TestClass]
    public class BiblicalArchaeologyGatewayTests
    {
        [TestMethod]
        [Ignore]
        public void GetAll_ShouldGetThem()
        {
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient().Returns(new HttpClient());
            var gateway = new BiblicalArchaeologyGateway(factory);
            var results = gateway.GetAll().Result;
            Assert.IsTrue(results.Count > 0);
        }
        
    }
}