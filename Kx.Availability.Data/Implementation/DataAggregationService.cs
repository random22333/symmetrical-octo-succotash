using System.Data;
using System.Net;
using System.Net.Http.Json;
using Kx.Availability.Data.Exceptions;
using Kx.Availability.Data.Mongo.Models;
using Kx.Availability.Data.Mongo.StoredModels;
using Kx.Core.Common.Data;
using Kx.Core.Common.HelperClasses;
using Kx.Core.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Serilog;
// ReSharper disable TemplateIsNotCompileTimeConstantProblem

// ReSharper disable PossibleMultipleEnumeration
/*
This class has several private fields that are initialized in the constructor:

_tenant - an ITenant dependency injected for the current tenant
_httpClientFactory - for making HTTP requests
_aggregateData - provides access to perform data aggregation
_locationsData and _roomsData - give access to temporary data storage
_coreBedroomsUrl and _coreLocationsUrl - URLs to fetch bedroom and location data from
_pageSize - number of records to fetch per API request
The main purpose of this class is to orchestrate:

Fetching location data from an API endpoint
Fetching bedroom data from another API endpoint
Storing the fetched data into temporary collections
Combining the temporary data into a final aggregated data model
It achieves this through several key methods:

CleanTenantTempTablesAsync() - Clears out the temporary data collections
CreateLocationsIndexes() and CreateRoomsIndexes() - Sets up indexes on the temp collections
DoLocationsAsync() and DoRoomsAsync() - Orchestrates fetching paginated data from the APIs
MashTempTablesIntoTheAvailabilityModelAsync() - Combines the temp data into the final model
The overall flow is:

Constructor initializes dependencies
ReloadOneTenantsDataAsync() orchestrates the overall process
It clears temp data, creates indexes, fetches location and bedroom data
Fetched data is stored in temporary collections
Finally it mashes the temp data into the unified data model
So in summary, this class fetches data from multiple sources, stores it temporarily, 
then combines it into a final aggregated data model. The main logic is coordinating and orchestrating the data aggregation pipeline.
_aggregateData = DataAccessHelper.ParseAggregationDataAccess(dbAccessAggregate);

DataAccessFactory directly instantiates concrete data access classes. 
This makes it difficult to mock or swap implementations. Could use abstraction/factory instead.

There is tight coupling between the data access classes and MongoDB specific types like IMongoCollection. An abstraction could be used to
decouple the data access from the implementation.
The AggregatedAvailabilityData class has multiple responsibilities - it handles loading data, transforming it, saving state, etc. This could be split into separate classes 
for better separation of concerns.*/

namespace Kx.Availability.Data.Implementation;

public class DataAggregationService : IDataAggregationService
{
    private readonly ITenant _tenant;
    private readonly IHttpClientFactory _httpClientFactory;        
    private readonly IDataAccessAggregation _aggregateData;    
    private readonly string? _coreBedroomsUrl;
    private readonly string? _coreLocationsUrl;
    private readonly IDataAggregationStoreAccess<LocationsDataStoreModel> _locationsData;
    private readonly IDataAggregationStoreAccess<BedroomsDataStoreModel> _roomsData;    
    private readonly int _pageSize;
    private readonly string? _mongoId;         
        

    public DataAggregationService(IDataAccessFactory dataAccessFactory, ITenant tenant, IConfiguration config,
        IHttpClientFactory httpClientFactory)
    {

        _tenant = tenant;
        _httpClientFactory = httpClientFactory;                        

        var dbAccessAggregate = dataAccessFactory.GetDataAccess(KxDataType.AvailabilityAggregation);
        _aggregateData = DataAccessHelper.ParseAggregationDataAccess(dbAccessAggregate);

        _locationsData = dataAccessFactory.GetDataStoreAccess<LocationsDataStoreModel>();
        _roomsData = dataAccessFactory.GetDataStoreAccess<BedroomsDataStoreModel>();
        
        
        _coreBedroomsUrl = config.GetSection("BEDROOMS_URL").Value;
        _coreLocationsUrl = config.GetSection("LOCATIONS_URL").Value;        
        _mongoId = config.GetSection("MongoID").Value ?? null;        
        
        _pageSize = 1000;
        if (int.TryParse(config.GetSection("DEFAULT_PAGE_SIZE").Value, out var pageSize))
        {
            _pageSize = pageSize;
        }        
    }

    private async Task CreateIndexes()
    {
        await CreateLocationsIndexes();
        await CreateRoomsIndexes();
    }

    private async Task CreateLocationsIndexes()
    {
        var indexBuilder = Builders<LocationsDataStoreModel>.IndexKeys;
        var indexModel = new CreateIndexModel<LocationsDataStoreModel>(indexBuilder
            .Ascending(x => x.ExternalId)
            .Ascending(x => x.Type)
            .Ascending(x => x.Id)
            .Ascending(p => p.ParentId));
        await _locationsData.AddIndex(indexModel);
    }

    private async Task CreateRoomsIndexes()
    {
        var indexBuilder = Builders<BedroomsDataStoreModel>.IndexKeys;
        var indexModel = new CreateIndexModel<BedroomsDataStoreModel>(indexBuilder.Ascending(x => x.RoomId));
        await _roomsData.AddIndex(indexModel);
    }  

    public async Task<(HttpStatusCode statusCode, string result)> ReloadOneTenantsDataAsync()
    {
        try
        {            
            
            _aggregateData.StartStateRecord();
            
            Log.Information("Cleaning tmp table");
            await CleanTenantTempTablesAsync();
            
            await CreateIndexes();
                                    
            //1. Get Locations 
            var locationsTask = DoLocationsAsync();
                                    
            //2. Get the rooms
            var roomsTask = DoRoomsAsync();
                                            
            await Task.WhenAll(locationsTask, roomsTask);
            
            //3. Mash them together
            //make the main table from all imported tables            
            await MashTempTablesIntoTheAvailabilityModelAsync();
            
            //4. save tenantAvailabilityModel.            
            await MoveTempTenantToLive();                        
            await CleanTenantTempTablesAsync();
                        
            return (HttpStatusCode.NoContent, string.Empty);
            
        }
        catch (Exception ex)
        {
            return (HttpStatusCode.ExpectationFailed, ex.Message);
        }
    }

    private async Task MashTempTablesIntoTheAvailabilityModelAsync()
    {
        try
        {
            
            var aggregatedAvailabilityModel = GetAggregatedDataStoreModel();

            var rooms = _roomsData.QueryFreely();

            if (rooms is null || !rooms.Any()) throw new DataException();                                   
            
            foreach (var room in rooms)
            {                                
                var availabilityModel = CreateAvailabilityMongoModel();
                availabilityModel.ID = _mongoId;
                if (_mongoId is null)
                {
                    availabilityModel.ID = Convert.ToString(availabilityModel.GenerateNewID());
                }

                availabilityModel.TenantId = _tenant.TenantId;
                availabilityModel.RoomId = room.RoomId;
                
                var addLocations = AddLocationModels(room);
                availabilityModel.Locations.AddRange(addLocations);
                
                aggregatedAvailabilityModel.Availability.Add(availabilityModel);
            }
          
            await _aggregateData.InsertAsync(aggregatedAvailabilityModel);
          
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to mash data together: {ex}");
            throw;
        }
    }    

    private AvailabilityMongoModel CreateAvailabilityMongoModel()
    {
        return new AvailabilityMongoModel
        {
            TenantId = _tenant.TenantId,
            RoomId = string.Empty, 
            Locations = new List<LocationModel>()
        };
    }

   
    private IEnumerable<LocationModel> AddLocationModels(BedroomsDataStoreModel room)
    {
        try
        {
            var locationsQuery = _locationsData.QueryFreely();

            /* Add the direct parent area */
            var tempLocations =
                locationsQuery?
                    .Where(l => l.Id == room.LocationID 
                                && (l.Type.ToLower() != "area" && l.Type.ToLower() != "site"))
                    .Select(loc => new LocationModel
                    {
                        Id = loc.Id,
                        Name = loc.Name,
                        ParentId = loc.ParentId,
                        IsDirectLocation = true
                    }).ToList();


            if (!(tempLocations?.Count > 0))
                return tempLocations as IEnumerable<LocationModel> ?? new List<LocationModel>();


            var currentTopLevelAreaIndex = 0;
            
            while (!tempLocations.Exists(x => x.ParentId == null))
            {
                var parentLocation = tempLocations[currentTopLevelAreaIndex].ParentId;

                var nextParentLocation =
                    locationsQuery?
                        .Where(l => l.Id == parentLocation)
                        .Select(loc => new LocationModel
                        {
                            Id = loc.Id,
                            Name = loc.Name,
                            ParentId = loc.ParentId,
                            IsDirectLocation = true
                        });

                if (nextParentLocation != null && nextParentLocation.Any())
                {
                    tempLocations.AddRange(nextParentLocation.ToList());
                    currentTopLevelAreaIndex++;
                }
                else
                {
                    Log.Error(
                        $"The location has a parent Id where the location does not exist ParentId: {parentLocation}");
                    break;
                }

                if (currentTopLevelAreaIndex >= tempLocations.Count) break;
                
            }


            return tempLocations as IEnumerable<LocationModel> ?? new List<LocationModel>();

        }
        catch (Exception ex)
        {
            Task.FromResult(async () => await LogStateErrorsAsync(LocationType.Locations, ex));
            throw;
        }
    }

    private AggregatedAvailabilityModel GetAggregatedDataStoreModel()
    {
        var data = new AggregatedAvailabilityModel
        {
            TenantId = _tenant.TenantId
        };
        return data;
    }  

    public async Task InsertStateAsync(ITenantDataModel item)
    {
        await _aggregateData.InsertStateAsync(item);
    }

    private async Task DoLocationsAsync()
    {
        try
        {
            //paginate
            var pageOfLocations = await GetLocationsAsync(pageNo: 1);         
            await _locationsData.InsertPageAsync<PaginatedStoreModel<LocationsDataStoreModel>>(pageOfLocations);

            if (pageOfLocations.TotalPages > 1)
            {
                for (var i = 2; i <= pageOfLocations.TotalPages; i++)
                {
                    var page = await GetLocationsAsync(pageNo: i);
                    await _locationsData.InsertPageAsync<PaginatedStoreModel<LocationsDataStoreModel>>(page);
                }
            }
        }
        catch (Exception ex)
        {
            await LogStateErrorsAsync(LocationType.Locations, ex);
            throw;
        }
    }

    private async Task DoRoomsAsync()
    {
        try
        {          
            var pageOfRooms = await GetRoomsFromBedroomsApiAsync();
            await _roomsData.InsertPageAsync<PaginatedStoreModel<BedroomsDataStoreModel>>(pageOfRooms);
         

            if (pageOfRooms.TotalPages > 1)
            {
         
                for (var i = 2; i <= pageOfRooms.TotalPages; i++)
                {
         
                    var page = await GetRoomsFromBedroomsApiAsync(i);
                    await _roomsData.InsertPageAsync<PaginatedStoreModel<BedroomsDataStoreModel>>(page);
                }         
            }
        }
        catch (Exception ex)
        {
            await LogStateErrorsAsync(LocationType.Rooms, ex);
            throw;
        }
    }



    private async Task<IPaginatedModel<BedroomsDataStoreModel>> GetRoomsFromBedroomsApiAsync(int pageNo = 1)
    {       
        var uriBuilder = new UriBuilder(_coreBedroomsUrl!)
        {
            Path = $"production/v1/{_tenant.TenantId}/bedrooms/rooms",
            Query = $"pageSize={_pageSize}&page={pageNo}"
        };
        var httpClient = _httpClientFactory.CreateClient(nameof(BedroomsDataStoreModel));

        return await GetDataFromApiAsync<BedroomsDataStoreModel>(uriBuilder, httpClient);
    }



    private async Task<IPaginatedModel<LocationsDataStoreModel>> GetLocationsAsync(int pageNo = 1)
    {
        var uriBuilder = new UriBuilder(_coreLocationsUrl!)
        {
            Path = $"production/v1/{_tenant.TenantId}/locations",
            Query = $"pageSize={_pageSize}&page={pageNo}"
        };
        var httpClient = _httpClientFactory.CreateClient(nameof(LocationsDataStoreModel));

        return await GetDataFromApiAsync<LocationsDataStoreModel>(uriBuilder, httpClient);
    }



    public async Task<IPaginatedModel<T>> GetDataFromApiAsync<T>(UriBuilder uriBuilder, HttpClient httpClient)
    {
        var response = await httpClient.GetAsync(uriBuilder.ToString());
        return await response.Content.ReadFromJsonAsync<PaginatedStoreModel<T>>() ??
               throw new UnprocessableEntityException();
    }

    private async Task MoveTempTenantToLive()
    {
        await _aggregateData.UpdateAsync();
    }

    private async Task CleanTenantTempTablesAsync()
    {
        await _locationsData.DeleteAsync();
        await _roomsData.DeleteAsync();        
    }

    private async Task LogStateErrorsAsync(LocationType changeTableType, Exception ex)
    {
        await LogStateErrorsAsync(changeTableType.ToString(), ex);
    }
    
    private async Task LogStateErrorsAsync(string changeType, Exception ex)
    {
        await _aggregateData.UpdateStateAsync(
            StateEventType.CycleError,
            true,
            ex.ToString());

        Log.Logger.Error(
            "Error inserting {S}{FullMessage}",
            changeType,
            ex.ToString());
    }
}
