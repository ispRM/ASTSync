using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ASTSync.BatchTableHelper;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using Microsoft.Graph.Beta.Models.ODataErrors;
using Microsoft.Graph.Beta.Reports.Security.GetAttackSimulationTrainingUserCoverage;
using Azure;
using System.Linq;

namespace ASTSync;

public static class Sync
{

    // If to pull entra users
    private static bool _pullEntraUsers =
        bool.Parse(Environment.GetEnvironmentVariable("SyncEntra", EnvironmentVariableTarget.Process) ?? "true");
    
    /// <summary>
    /// How many table rows to send up in a batch
    /// </summary>
    private static int _maxTableBatchSize = 100;

    /// <summary>
    /// How frequent to sync users (default 7 days)
    /// </summary>
    private static TimeSpan _ageUserSync = TimeSpan.FromDays(7);

    /// <summary>
    /// Maintains a list of users we have already synced
    /// </summary>
    private static ConcurrentDictionary<string, bool> userListSynced = new();
    
    /// <summary>
    /// Logger
    /// </summary>
    private static ILogger _log { get; set; }
    
    /// <summary>
    /// Batch table processor for Entra Users
    /// </summary>
    private static BatchTable _batchUsers { get; set; }
    
    /// <summary>
    /// Batch table processor for Simulations
    /// </summary>
    private static BatchTable _batchSimulations { get; set; }
    
    /// <summary>
    /// Batch table processor for Simulation Users
    /// </summary>
    private static BatchTable _batchSimulationUsers { get; set; }
    
    /// <summary>
    /// Batch table processor for Simulation User Events
    /// </summary>
    private static BatchTable _batchSimulationUserEvents { get; set; }
    
    /// <summary>
    /// Batch table processor for Trainings
    /// </summary>
    private static BatchTable _batchTrainings { get; set; }
    
    /// <summary>
    /// Batch table processor for Payloads
    /// </summary>
    private static BatchTable _batchPayloads { get; set; }
    
    /// <summary>
    /// Batch table processor for Training User Coverage
    /// </summary>
    private static BatchTable _batchTrainingUserCoverage { get; set; }
    
    [FunctionName("Sync")]
    public static async Task RunAsync([TimerTrigger("0 */15 * * * *")] TimerInfo myTimer, ILogger log)
    {
        
        Stopwatch sw = Stopwatch.StartNew();
        
        _log = log;
        // Get graph client
        var GraphClient = GetGraphServicesClient();
        
        _log.LogInformation($"AST Sync started at: {DateTime.UtcNow}");
        
        const string stateTableName = "StateTable";
        TableClient stateTable = new TableClient(GetStorageConnection(), stateTableName);
        await stateTable.CreateIfNotExistsAsync();
        int batchSize = int.TryParse(Environment.GetEnvironmentVariable("SimulationBatchSize", EnvironmentVariableTarget.Process), out int parsedSize) ? parsedSize : 15;
        _log.LogInformation($"Batch size set to {batchSize} simulations per run (via env var).");

        // Spin up the batch queue processors
        _batchUsers = new BatchTable(GetStorageConnection(), "Users", _maxTableBatchSize, log);
        _batchSimulations = new BatchTable(GetStorageConnection(), "Simulations", _maxTableBatchSize, log);
        _batchSimulationUsers = new BatchTable(GetStorageConnection(), "SimulationUsers", _maxTableBatchSize, log);
        _batchSimulationUserEvents = new BatchTable(GetStorageConnection(), "SimulationUserEvents", _maxTableBatchSize, log);
        _batchTrainings = new BatchTable(GetStorageConnection(), "Trainings", _maxTableBatchSize, log);
        _batchPayloads = new BatchTable(GetStorageConnection(), "Payloads", _maxTableBatchSize, log);
        _batchTrainingUserCoverage = new BatchTable(GetStorageConnection(), "TrainingUserCoverage", _maxTableBatchSize, log);
        
        // Sync Tenant Simulations to perform sync whilst sync'ing simulations to table
        // This can probably be moved to an async foreach below.
        // However, need to figure out how to do the async foreach return in a lambda (graph services client paging func.)

        HashSet<string> simulationIds;
        
        try
        {
             simulationIds = await GetTenantSimulations(GraphClient);
        }
        catch (Exception e)
        {
            _log.LogError($"Failed to get simulations: {e}");
            throw;
        }
        
        string lastSimulationId = null;
        try
        {
            var entity = await stateTable.GetEntityAsync<SyncStateEntity>("State", "LastProcessedSimulation");
            lastSimulationId = entity.Value.LastSimulationId;
            _log.LogInformation($"Resuming sync from simulation ID: {lastSimulationId}");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogInformation("No previous simulation checkpoint found. Starting from beginning.");
        }
        
        // Sync Tenant Simulation Users
        bool resume = string.IsNullOrEmpty(lastSimulationId);
        int processed = 0;
        string lastProcessed = null;

        foreach (var id in simulationIds.OrderBy(x => x))
        {
            if (!resume)
            {
                if (id == lastSimulationId)
                    resume = true;
                continue;
            }

            if (processed >= batchSize)
                break;

            try
            {
                _log.LogInformation($"Processing simulation {id} ({processed + 1}/{batchSize})...");
                await GetTenantSimulationUsers(GraphClient, id);
                lastProcessed = id;
                processed++;

                await stateTable.UpsertEntityAsync(new SyncStateEntity
                {
                    LastSimulationId = lastProcessed
                }, TableUpdateMode.Replace);
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to get simulation users for simulation {id}: {e}");
            }
        }

        if (processed == 0)
        {
            _log.LogInformation("No simulations processed in this run.");
        }
        else
        {
            _log.LogInformation($"Processed {processed} simulations in this run. Last processed ID: {lastProcessed}");
        }

        // Reset checkpoint solo se siamo arrivati in fondo
        if (!string.IsNullOrEmpty(lastProcessed) && lastProcessed == simulationIds.OrderBy(x => x).Last())
        {
            _log.LogInformation("All simulations have been processed. Resetting checkpoint.");
            try
            {
                await stateTable.DeleteEntityAsync("State", "LastProcessedSimulation");
            }
            catch (RequestFailedException e) when (e.Status == 404) { }
        }

            
        
        // Remaining syncs
        try
        {
            await GetTrainings(GraphClient);
        }
        catch (Exception e)
        {
            _log.LogError($"Failed to get trainings: {e}");
        }
        
        // Get tenant payloads
        try
        {
            await GetPayloads(GraphClient, SourceType.Tenant);
        }
        catch (Exception e)
        {
            _log.LogError($"Failed to get tenant payloads: {e}");
        }
        
        // Get global payloads
        try
        {
            await GetPayloads(GraphClient, SourceType.Global);
        }
        catch (Exception e)
        {
            _log.LogError($"Failed to get global payloads: {e}");
        }
        
        // Get training user coverage
        try
        {
            await GetTrainingUserCoverage(GraphClient);
        }
        catch (Exception e)
        {
            _log.LogError($"Failed to get training user coverage: {e}");
        }

        // Dispose of all batch processors
        await _batchUsers.DisposeAsync();
        await _batchSimulations.DisposeAsync();
        await _batchSimulationUsers.DisposeAsync();
        await _batchSimulationUserEvents.DisposeAsync();
        await _batchTrainings.DisposeAsync();
        await _batchPayloads.DisposeAsync();
        await _batchTrainingUserCoverage.DisposeAsync();
        
        _log.LogInformation($"AST sync completed synchronising {simulationIds.Count} simulations in {sw.Elapsed}");
        
    }
    
    
    /// <summary>
    /// Get Simulations for Tenant
    /// </summary>
    /// <param name="GraphClient"></param>
    private static async Task<HashSet<string>> GetTenantSimulations(GraphServiceClient GraphClient)
    {
        
        // Simulation Ids
        HashSet<string> SimulationIds = new HashSet<string>();
        
        // Get table client for table
        TableClient tableClient = new TableClient(GetStorageConnection(), "Simulations");

        // Get simulation results
        var results = await GraphClient
            .Security
            .AttackSimulation
            .Simulations
            .GetAsync((requestConfiguration) =>
            {
                requestConfiguration.QueryParameters.Top = 1000;
            });

        var pageIterator = Microsoft.Graph.PageIterator<Simulation,SimulationCollectionResponse>
            .CreatePageIterator(GraphClient, results, async (sim) =>
            {
                // Get the table row item for this simulation
                var SimulationExistingTableItem = await tableClient.GetEntityIfExistsAsync<TableEntity>("Simulations", sim.Id);

                // For determining the last user sync
                DateTime LastUserSync = DateTime.SpecifyKind(new DateTime(1986,1,1), DateTimeKind.Utc);
                if (SimulationExistingTableItem.HasValue && SimulationExistingTableItem.Value.ContainsKey("LastUserSync"))
                    LastUserSync = DateTime.SpecifyKind(DateTime.Parse(SimulationExistingTableItem.Value["LastUserSync"].ToString()), DateTimeKind.Utc);
                
                // Perform a user sync (if)
                // - We have never performed a sync
                // - The simulation finished within the past 7 days
                // - Or the simulation is running
                // - Last user sync is more than a month ago
                
                if (!SimulationExistingTableItem.HasValue || 
                    sim.Status == SimulationStatus.Running || 
                    sim.CompletionDateTime > DateTime.UtcNow.AddDays(-7) || 
                    (LastUserSync < DateTime.UtcNow.AddMonths(-1)))
                {
                    _log.LogInformation($"Perform full synchronisation of simulation '{sim.DisplayName}' status {SimulationStatus.Running}");
                    SimulationIds.Add(sim.Id);
                }
                
                // Add the table item
                _batchSimulations.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity("Simulations", sim.Id)
                {
                    {"AttackTechnique", sim.AttackTechnique.ToString()},
                    {"AttackType", sim.AttackType.ToString()},
                    {"AutomationId", sim.AutomationId},
                    {"CompletionDateTime", sim.CompletionDateTime},
                    {"CreatedBy_Id", sim.CreatedBy?.Id},
                    {"CreatedBy_DisplayName", sim.CreatedBy?.DisplayName},
                    {"CreatedBy_Email", sim.CreatedBy?.Email},
                    {"CreatedDateTime", sim.CreatedDateTime},
                    {"Description",sim.Description},
                    {"DisplayName",sim.DisplayName},
                    {"DurationInDays", sim.DurationInDays},
                    {"IsAutomated", sim.IsAutomated},
                    {"LastModifiedBy_Id", sim.LastModifiedBy?.Id},
                    {"LastModifiedBy_DisplayName", sim.LastModifiedBy?.DisplayName},
                    {"LastModifiedBy_Email", sim.LastModifiedBy?.Email},
                    {"LastModifiedDateTime", sim.LastModifiedDateTime},
                    {"Payload_Id", sim.Payload?.Id},
                    {"Payload_DisplayName", sim.Payload?.DisplayName},
                    {"Payload_Platform", sim.Payload?.Platform?.ToString()},
                    {"Status", sim.Status.ToString()},
                    {"AutomationId", sim.AutomationId},
                    {"LastUserSync", LastUserSync}
                }));
                
                return true; 
            });

        await pageIterator.IterateAsync();
        
        // Flush batch simulations
        await _batchSimulations.FlushBatchAsync(TimeSpan.FromMinutes(5));
        
        return SimulationIds;
    }

    /// <summary>
    /// Get Trainings
    /// </summary>
    /// <param name="GraphClient"></param>
    private static async Task<bool> GetTrainings(GraphServiceClient GraphClient)
    {
        
        Stopwatch sw = Stopwatch.StartNew();
        _log.LogInformation("Synchronising trainings");
        
        // Get simulation results
        var results = await GraphClient
            .Security
            .AttackSimulation
            .Trainings
            .GetAsync((requestConfiguration) =>
            {
                requestConfiguration.QueryParameters.Top = 1000;
            });

        var pageIterator = Microsoft.Graph.PageIterator<Training,TrainingCollectionResponse>
            .CreatePageIterator(GraphClient, results, async (training) =>
            {
                
                // Add the table item
                _batchTrainings.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity("Trainings", training.Id)
                {
                    {"TrainingId", training.Id},
                    {"DisplayName", training.DisplayName},
                    {"Description", training.Description},
                    {"DurationInMinutes", training.DurationInMinutes},
                    {"Source", training.Source.ToString()},
                    {"Type", training.Type?.ToString()},
                    {"availabilityStatus", training.AvailabilityStatus?.ToString()},
                    {"HasEvaluation", training.HasEvaluation},
                    {"CreatedBy_Id", training.CreatedBy?.Id},
                    {"CreatedBy_DisplayName", training.CreatedBy?.DisplayName},
                    {"CreatedBy_Email", training.CreatedBy?.Email},
                    {"LastModifiedBy_Id", training.LastModifiedBy?.Id},
                    {"LastModifiedBy_DisplayName", training.LastModifiedBy?.DisplayName},
                    {"LastModifiedBy_Email", training.LastModifiedBy?.Email},
                    {"LastModifiedDateTime", training.LastModifiedDateTime},
                }));
                
                return true; 
            });

        await pageIterator.IterateAsync();
        
        // Flush remaining trainings
        await _batchTrainings.FlushBatchAsync(TimeSpan.FromMinutes(5));
        
        _log.LogInformation($"Synchronising trainings complete in {sw.Elapsed}");

        return true;
    }
    
    /// <summary>
    /// Get Training User Coverage
    /// </summary>
    /// <param name="GraphClient"></param>
    private static async Task<bool> GetTrainingUserCoverage(GraphServiceClient GraphClient)
    {
        
        Stopwatch sw = Stopwatch.StartNew();
        _log.LogInformation("Synchronising training user coverage");
        
        // Get simulation results
        var results = await GraphClient
            .Reports
            .Security
            .GetAttackSimulationTrainingUserCoverage.
            GetAsGetAttackSimulationTrainingUserCoverageGetResponseAsync((requestConfiguration) =>
            {
                requestConfiguration.QueryParameters.Top = 1000;
            });

        var pageIterator = Microsoft.Graph.PageIterator<AttackSimulationTrainingUserCoverage,GetAttackSimulationTrainingUserCoverageGetResponse>
            .CreatePageIterator(GraphClient, results, async (trainingUser) =>
            {
                
                // Add the table item
                if (trainingUser.UserTrainings is not null && trainingUser.AttackSimulationUser is not null)
                {
                    foreach (var trainingAssignment in trainingUser.UserTrainings)
                    {
                        // AssignedDateTime is marked as nullable but API shouldn't be returning null
                        // Check anyway.
                        if (trainingAssignment.AssignedDateTime is not null)
                        {
                            // API doesn't return training id but instead a display name
                            // Display name could have characters which azure table keys are sensitive to, strip with a hash.
                            // This is used for the rowkey
                            
                            var hashDisplay = GetHashFromString(trainingAssignment.DisplayName);
                            
                            _batchTrainingUserCoverage.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity("TrainingUserCoverage", $"{trainingUser.AttackSimulationUser.UserId}{hashDisplay}{trainingAssignment.AssignedDateTime?.ToString("yyyyMMddHHmmss")}" )
                            {
                                {"UserId", trainingUser.AttackSimulationUser.UserId},
                                {"DisplayName", trainingAssignment.DisplayName},
                                {"AssignedDateTime", trainingAssignment.AssignedDateTime},
                                {"CompletionDateTime", trainingAssignment.CompletionDateTime},
                                {"TrainingStatus", trainingAssignment.TrainingStatus.ToString()}
                            }));
                        }
                    }
                }
                
                return true; 
            });

        await pageIterator.IterateAsync();
        
        // Flush remaining trainings
        await _batchTrainingUserCoverage.FlushBatchAsync(TimeSpan.FromMinutes(5));
        
        _log.LogInformation($"Synchronising trainings user coverage complete in {sw.Elapsed}");

        return true;
    }
    
    
    /// <summary>
    /// Get Payloads
    /// </summary>
    /// <param name="GraphClient"></param>
    private static async Task<bool> GetPayloads(GraphServiceClient GraphClient, SourceType Source)
    {
        
        Stopwatch sw = Stopwatch.StartNew();
        _log.LogInformation("Synchronising payloads");

        string? filter = null;

        if (Source == SourceType.Global)
            filter = "source eq 'global'";

        if (Source == SourceType.Tenant) 
            filter = "source eq 'tenant'";
        
        // Get simulation results
        var results = await GraphClient
            .Security
            .AttackSimulation
            .Payloads
            .GetAsync((requestConfiguration) =>
            {
                requestConfiguration.QueryParameters.Top = 1000;
                requestConfiguration.QueryParameters.Filter = filter;
            });

        var pageIterator = Microsoft.Graph.PageIterator<Payload,PayloadCollectionResponse>
            .CreatePageIterator(GraphClient, results, async (payload) =>
            {
                
                // Add the table item
                _batchPayloads.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity("Payloads", payload.Id)
                {
                    {"PayloadId", payload.Id},
                    {"DisplayName", payload.DisplayName},
                    {"Description", payload.Description},
                    {"SimulationAttackType", payload.SimulationAttackType?.ToString()},
                    {"Platform", payload.Platform?.ToString()},
                    {"Status", payload.Status?.ToString()},
                    {"Source", payload.Source?.ToString()},
                    {"PredictedCompromiseRate", payload.PredictedCompromiseRate},
                    {"Complexity", payload.Complexity?.ToString()},
                    {"Technique", payload.Technique?.ToString()},
                    {"Theme", payload.Theme?.ToString()},
                    {"Brand", payload.Brand?.ToString()},
                    {"Industry", payload.Industry?.ToString()},
                    {"IsCurrentEvent", payload.IsCurrentEvent},
                    {"IsControversial", payload.IsControversial},
                    {"CreatedBy_Id", payload.CreatedBy?.Id},
                    {"CreatedBy_DisplayName", payload.CreatedBy?.DisplayName},
                    {"CreatedBy_Email", payload.CreatedBy?.Email},
                    {"LastModifiedBy_Id", payload.LastModifiedBy?.Id},
                    {"LastModifiedBy_DisplayName", payload.LastModifiedBy?.DisplayName},
                    {"LastModifiedBy_Email", payload.LastModifiedBy?.Email},
                    {"LastModifiedDateTime", payload.LastModifiedDateTime},
                }));
                
                return true; 
            });

        await pageIterator.IterateAsync();

        // Flush remaining payloads
        await _batchPayloads.FlushBatchAsync(TimeSpan.FromMinutes(5));
        
        _log.LogInformation($"Synchronising payloads complete in {sw.Elapsed}");

        return true;
    }
    
    /// <summary>
    /// Get Simulations Users
    /// </summary>
    /// <param name="GraphClient"></param>
    private static async Task GetTenantSimulationUsers(GraphServiceClient GraphClient, string SimulationId)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _log.LogInformation($"Performing full user synchronisation of {SimulationId}");

        var requestInformation =
            GraphClient.Security.AttackSimulation.Simulations[SimulationId].ToGetRequestInformation();

        requestInformation.URI = new Uri(requestInformation.URI.ToString() + "/report/simulationUsers");
        requestInformation.QueryParameters["Top"] = 1000;

        var results = await GraphClient.RequestAdapter.SendAsync<UserSimulationDetailsCollectionResponse>(requestInformation, UserSimulationDetailsCollectionResponse.CreateFromDiscriminatorValue);

        var pageIterator = Microsoft.Graph.PageIterator<UserSimulationDetails,UserSimulationDetailsCollectionResponse>
            .CreatePageIterator(GraphClient, results, async (userSimDetail) =>
            {
                // Create an identifier for the SimulationUser_Id
                string id = $"{SimulationId}-{userSimDetail.SimulationUser?.UserId}";
                bool hasClicked = false;
                DateTimeOffset? emailLinkClickedDateTime = null;

                // Add simulation user events in to table
                if (userSimDetail.SimulationEvents is not null)
                {
                    foreach (var simulationUserEvents in userSimDetail.SimulationEvents)
                    {
                        if (simulationUserEvents.EventName == "EmailLinkClicked")
                        {
                            hasClicked = true;
                            emailLinkClickedDateTime = simulationUserEvents.EventDateTime;
                        }
                        _batchSimulationUserEvents.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity(SimulationId, $"{userSimDetail.SimulationUser?.UserId}_{simulationUserEvents.EventName}_{simulationUserEvents.EventDateTime.Value.ToUnixTimeSeconds()}")
                        {
                            {"SimulationUser_Id", id},
                            {"SimulationUser_UserId", userSimDetail.SimulationUser?.UserId},
                            {"SimulationUserEvent_EventName", simulationUserEvents.EventName},
                            {"SimulationUserEvent_EventDateTime", simulationUserEvents.EventDateTime},
                            {"SimulationUserEvent_Browser", simulationUserEvents.Browser},
                            {"SimulationUserEvent_IpAddress", simulationUserEvents.IpAddress},
                            {"SimulationUserEvent_OsPlatformDeviceDetails", simulationUserEvents.OsPlatformDeviceDetails},
                        }));

                    }

                }

                // Add the table item
                _batchSimulationUsers.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity(SimulationId, userSimDetail.SimulationUser?.UserId)
                {
                    {"SimulationUser_Id", id},
                    {"SimulationId", SimulationId},
                    {"SimulationUser_UserId", userSimDetail.SimulationUser?.UserId},
                    {"SimulationUser_Email", userSimDetail.SimulationUser?.Email},
                    {"CompromisedDateTime", userSimDetail.CompromisedDateTime},
                    {"ReportedPhishDateTime", userSimDetail.ReportedPhishDateTime},
                    {"AssignedTrainingsCount", userSimDetail.AssignedTrainingsCount},
                    {"CompletedTrainingsCount", userSimDetail.CompletedTrainingsCount},
                    {"InProgressTrainingsCount", userSimDetail.InProgressTrainingsCount},
                    {"IsCompromised", userSimDetail.IsCompromised},
                    {"HasReported", userSimDetail.ReportedPhishDateTime is not null},
                    {"HasClicked", hasClicked},
                    {"EmailLinkClickedDateTime", emailLinkClickedDateTime},
                }));
                
                // Determine if should sync user
                if (await ShouldSyncUser(userSimDetail.SimulationUser?.UserId))
                {
                    await SyncUser(GraphClient, userSimDetail.SimulationUser?.UserId);
                }
                
                return true; 
            });

        await pageIterator.IterateAsync();
        
        // update in the Simulations table that this has been syncd
        _batchSimulations.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpsertMerge, new TableEntity("Simulations", SimulationId)
        {
            {"LastUserSync", DateTime.UtcNow},
        }));
        
        // Flush batch simulations for users and events
        await _batchSimulationUsers.FlushBatchAsync(TimeSpan.FromMinutes(5));
        await _batchSimulationUserEvents.FlushBatchAsync(TimeSpan.FromMinutes(5));
        
        _log.LogInformation($"Full user synchronisation of {SimulationId} completed in {sw.Elapsed}");

    }
    
    /// <summary>
    /// Get the Graph Client for Tenant
    /// </summary>
    /// <returns></returns>
    private static GraphServiceClient GetGraphServicesClient()
    {
        // Use default azure credential
        var tokenCredential = new DefaultAzureCredential();
        
        // Default graph scope
        var scopes = new[] { "https://graph.microsoft.com/.default" };

        // Return graph services client
        return new GraphServiceClient(tokenCredential, scopes);
    }
    
    /// <summary>
    /// Get Storage Connection from App settings
    /// </summary>
    /// <returns></returns>
    private static string GetStorageConnection() => Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);

    /// <summary>
    /// Synchronise user
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    private static async Task SyncUser(GraphServiceClient GraphClient, string id)
    {
        // Set in dictionary
        userListSynced[id] = true;
        
        try
        {
            var User = await GraphClient.Users[id].GetAsync();

            if (User is not null)
            {
                _batchUsers.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpdateReplace, new TableEntity("Users", id)
                {
                    {"DisplayName", User.DisplayName},
                    {"GivenName", User.GivenName},
                    {"Surname", User.Surname},
                    {"Country", User.Country},
                    {"Mail", User.Mail},
                    {"Department", User.Department},
                    {"CompanyName", User.CompanyName},
                    {"City", User.City},
                    {"Country", User.Country},
                    {"JobTitle", User.JobTitle},
                    {"accountEnabled", User.AccountEnabled.ToString()},
                    {"LastUserSync", DateTime.UtcNow},
                    {"Exists", "true"},
                }));
            }

        }
        catch (ODataError e)
        {
            if (e.Error is not null && e.Error.Code == "Request_ResourceNotFound")
            {
                // User no longer exists, update table entity
                _batchUsers.EnqueueUpload(new TableTransactionAction(TableTransactionActionType.UpsertMerge, new TableEntity("Users", id)
                {
                    {"Exists", "false"},
                    {"LastUserSync", DateTime.UtcNow},
                }));
            }
            else
            {
                _log.LogError($"Failed to sync user {id}: {e}");
            }
            
        }
    }
    
    /// <summary>
    /// Determine if should sync user
    ///
    /// This prevents continously syncing the user
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    private static async Task<bool> ShouldSyncUser(string id)
    {
        // Return false if set not to sync users
        if (!_pullEntraUsers)
            return false;
        
        // Return false if already synchronised
        if (userListSynced.ContainsKey(id))
            return false;
        
        // Get the table entity to determine how long ago the user has been pulled
        TableClient tableClient = new TableClient(GetStorageConnection(), "Users");
        var UserTableItem = await tableClient.GetEntityIfExistsAsync<TableEntity>("Users", id);
                
        // Get last sync time
        DateTime LastUserSync = new DateTime(1986,1,1);
        if (UserTableItem.HasValue && UserTableItem.Value.ContainsKey("LastUserSync"))
            LastUserSync = DateTime.SpecifyKind(DateTime.Parse(UserTableItem.Value["LastUserSync"].ToString()), DateTimeKind.Utc);

        // If no sync or days is older than a week
        if (LastUserSync < DateTime.UtcNow.Subtract(_ageUserSync))
            return true;
           
        // Add to userSyncList so we don't need to check again
        userListSynced[id] = true;

        return false;

    }

    /// <summary>
    /// Generates an MD5 hash from string for the purpose of creating rowkeys that 
    /// will not contain special characters that azure table is sensitive to.
    /// MD5 is okay here as a) this is not crytographically sensitive data and b) there is a very low chance
    /// of collision.
    /// MD5 is a good pay off of storage space vs uniqueness
    /// </summary>
    /// <param name="Text"></param>
    /// <returns></returns>
    private static string GetHashFromString(string Text)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] inputBytes = Encoding.ASCII.GetBytes(Text);
            byte[] hashBytes = md5.ComputeHash(inputBytes);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("X2"));
            }

            return sb.ToString();
        }
    }
}

/// <summary>
/// Source Type, Global or Tenant - for filters
/// </summary>
public enum SourceType
{
    /// <summary>
    /// Global (default payloads)
    /// </summary>
    Global,
    
    /// <summary>
    /// Tenant specific
    /// </summary>
    Tenant
}