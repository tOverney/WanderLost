﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WanderLost.Shared.Data;
using WanderLost.Shared.Interfaces;

namespace WanderLost.Server.Controllers;

public class MerchantHub : Hub<IMerchantHubClient>, IMerchantHubServer
{
    public static string Path { get; } = "MerchantHub";

    private readonly DataController _dataController;
    private readonly MerchantsDbContext _merchantsDbContext;
    private readonly IConfiguration _configuration;

    public MerchantHub(DataController dataController, MerchantsDbContext merchantsDbContext, IConfiguration configuration)
    {
        _dataController = dataController;
        _merchantsDbContext = merchantsDbContext;
        _configuration = configuration;
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = nameof(RareCombinationRestricted))]
    public async Task UpdateMerchant(string server, ActiveMerchant merchant)
    {
        if (merchant is null) return;

        if (!await IsValidServer(server)) return;

        var allMerchantData = await _dataController.GetMerchantData();
        if (!merchant.IsValid(allMerchantData)) return;

        var merchantGroup = await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server == server && g.MerchantName == merchant.Name && g.AppearanceExpires > DateTimeOffset.Now)
            .Include(g => g.ActiveMerchants)
            .FirstOrDefaultAsync();

        if(merchantGroup == null)
        {
            //Merchant hasn't been saved to DB yet, get the in-memory one with expiration data calculated
            var activeMerchantGroups = await _dataController.GetActiveMerchantGroups(server);
            merchantGroup = activeMerchantGroups.FirstOrDefault(m => m.MerchantName == merchant.Name);

            //Don't allow modifying inactive merchants
            if (merchantGroup is null || !merchantGroup.IsActive) return;

            //Add it to the DB context to save later
            merchantGroup.Server = server;
            merchantGroup.MerchantName = merchant.Name;

            _merchantsDbContext.MerchantGroups.Add(merchantGroup);
        }

        var clientIp = GetClientIp();

        //If a client already uploaded a merchant, ignore further uploads and just skip out
        if (merchantGroup.ActiveMerchants.Any(m => m.UploadedBy == clientIp || (Context.UserIdentifier != null && m.UploadedByUserId == Context.UserIdentifier))) return;

        foreach (var existingMerchant in merchantGroup.ActiveMerchants)
        {
            if (existingMerchant.IsEqualTo(merchant))
            {
                if (existingMerchant.Hidden)
                {
                    existingMerchant.Hidden = false;
                    await _merchantsDbContext.SaveChangesAsync();

                    //Need to update the clients since we un-hid an item, also remove any other possible hidden items
                    merchantGroup.ActiveMerchants.RemoveAll(m => m.Hidden);

                    await Clients.Group(server).UpdateMerchantGroup(server, merchantGroup);
                }

                //Vote method attaches the merchant entity without database fetch to set the vote process flag
                //Before calling it clear out the change tracker so we don't get any duplicate entity exceptions
                _merchantsDbContext.ChangeTracker.Clear();
                //Found an existing matching merchant, so just upvote it instead
                await Vote(server, existingMerchant.Id, VoteType.Upvote);
                return;
            }
        }

        //Special handling case for banned users
        if (await HandleBans(clientIp, server, merchantGroup, merchant)) return;

        //If this client is uploading to multiple servers, ignore them
        if (await ClientHasOtherServerUploads(server, clientIp, Context.UserIdentifier)) return;

        merchant.UploadedBy = clientIp;
        merchant.UploadedByUserId = Context.UserIdentifier;
        merchant.RequiresProcessing = true;
        //Add an auto-upvote so the user can see their own submissions by default
        merchant.ClientVotes.Add(new Vote() { ClientId = clientIp, UserId = Context.UserIdentifier, VoteType = VoteType.Upvote });
        merchantGroup.ActiveMerchants.Add(merchant);

        await _merchantsDbContext.SaveChangesAsync();

        //Before we send to clients, remove anything that should be hidden
        merchantGroup.ActiveMerchants.RemoveAll(m => m.Hidden);

        await Clients.Group(server).UpdateMerchantGroup(server, merchantGroup);
    }

    private async Task<bool> HandleBans(string clientIp, string server, ActiveMerchantGroup group, ActiveMerchant merchant)
    {            
        //Skip out if no bans
        if (!await HasActiveBan(clientIp, Context.UserIdentifier)) return false;

        merchant.UploadedBy = clientIp;
        merchant.UploadedByUserId = Context.UserIdentifier;
        merchant.Hidden = true;
        //Add an auto-upvote so the user can see their own submissions by default
        merchant.ClientVotes.Add(new Vote() { ClientId = clientIp, UserId = Context.UserIdentifier, VoteType = VoteType.Upvote });

        group.ActiveMerchants.Add(merchant);

        await _merchantsDbContext.SaveChangesAsync();

        await Clients.Caller.UpdateMerchantGroup(server, group);

        return true;
    }

    private async Task<bool> ClientHasOtherServerUploads(string originalServer, string clientId, string? userId)
    {
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server != originalServer && g.AppearanceExpires > DateTimeOffset.Now)
            .AnyAsync(g => g.ActiveMerchants.Any(m => m.UploadedBy == clientId || (userId != null && m.UploadedByUserId == userId)));
    }

    public async Task Vote(string server, Guid merchantId, VoteType voteType)
    {
        var clientId = GetClientIp();

        Vote? existingVote;
        if (!string.IsNullOrWhiteSpace(Context.UserIdentifier))
        {
            existingVote = await _merchantsDbContext.Votes
               .Where(v => v.ActiveMerchantId == merchantId)
               .FirstOrDefaultAsync(v => v.UserId == Context.UserIdentifier);
        }
        else
        {
            existingVote = await _merchantsDbContext.Votes
               .Where(v => v.ActiveMerchantId == merchantId)
               .FirstOrDefaultAsync(v => v.ClientId == clientId);
        }
        if(existingVote is null)
        {
            var vote = new Vote()
            {
                ActiveMerchantId = merchantId,
                ClientId = clientId,
                UserId = Context.UserIdentifier,
                VoteType = voteType,
            };

            _merchantsDbContext.Votes.Add(vote);

            SetVoteProcessFlag(merchantId);

            await _merchantsDbContext.SaveChangesAsync();

            //Vote totals are tallied and sent by BackgroundVoteProcessor, just tell client their vote was counted
            await Clients.Caller.UpdateVoteSelf(merchantId, voteType);
        }
        else if(existingVote.VoteType != voteType)
        {
            existingVote.VoteType = voteType;

            SetVoteProcessFlag(merchantId);

            await _merchantsDbContext.SaveChangesAsync();

            //Vote totals are tallied and sent by BackgroundVoteProcessor, just tell client their vote was counted
            await Clients.Caller.UpdateVoteSelf(merchantId, voteType);
        }
    }

    private void SetVoteProcessFlag(Guid merchantId)
    {
        var updateMerchant = new ActiveMerchant()
        {
            Id = merchantId,
            RequiresVoteProcessing = true
        };
        _merchantsDbContext.Entry(updateMerchant).Property(m => m.RequiresVoteProcessing).IsModified = true;
    }

    public async Task SubscribeToServer(string server)
    {
        if (await IsValidServer(server))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, server);
        }
    }        

    public async Task UnsubscribeFromServer(string server)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, server);
    }

    private async Task<bool> IsValidServer(string server)
    {
        var regions = await _dataController.GetServerRegions();
        return regions.SelectMany(r => r.Value.Servers).Any(s => server == s);
    }

    public async Task<IEnumerable<ActiveMerchantGroup>> GetKnownActiveMerchantGroups(string server)
    {
        var clientIp = GetClientIp();
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server == server && g.AppearanceExpires > DateTimeOffset.Now)
            .Select(mg => new ActiveMerchantGroup
            {
                 Server = mg.Server,
                 MerchantName = mg.MerchantName,
                 ActiveMerchants = mg.ActiveMerchants
                                        .Where(m => !m.Hidden || (clientIp != null && m.UploadedBy == clientIp) || (Context.UserIdentifier != null && m.UploadedByUserId == Context.UserIdentifier))
                                        .ToList(),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<IEnumerable<Vote>> RequestClientVotes(string server)
    {
        if (!string.IsNullOrWhiteSpace(Context.UserIdentifier))
        {
            return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .AsNoTracking()
            .Where(g => g.Server == server && g.AppearanceExpires > DateTimeOffset.Now)
            .SelectMany(mg => mg.ActiveMerchants.SelectMany(m => m.ClientVotes))
            .Where(vote => vote.UserId == Context.UserIdentifier)
            .ToListAsync();
        }
        var clientIp = GetClientIp();
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .AsNoTracking()
            .Where(g => g.Server == server && g.AppearanceExpires > DateTimeOffset.Now)
            .SelectMany(mg => mg.ActiveMerchants.SelectMany(m => m.ClientVotes))
            .Where(vote => vote.ClientId == clientIp)
            .ToListAsync();
    }

    private string GetClientIp()
    {
#if DEBUG
        //In debug mode, allow using the connection ID to simulate multiple clients
        return Context.ConnectionId;
#endif

        //Check for header added by Nginx proxy
        //Potential security concern if this is not hosted behind a proxy that sets X-Real-IP,
        //that a malicious user could inject this header to fake address. Maybe make this configurable?
        var headers = Context.GetHttpContext()?.Request.Headers;
        if(headers?["X-Real-IP"].ToString() is string realIp && !string.IsNullOrWhiteSpace(realIp))
        {
            return realIp;
        }

        //Fallback for dev environment
        var remoteAddr = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        return remoteAddr;
    }

    public Task<bool> HasNewerClient(int version)
    {
        if (int.TryParse(_configuration["ClientVersion"], out var currentVersion))
        {
            return Task.FromResult(version < currentVersion);
        }
        //If the config is missing for some reason default to false
        return Task.FromResult(false);
    }
    private async Task<bool> HasActiveBan(string clientId, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var user = await _merchantsDbContext.Users
                .TagWithCallSite()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if(user is not null)
            {
                return user.BanExpires > DateTimeOffset.Now;
            }
        }

        return await _merchantsDbContext.Bans
            .TagWithCallSite()
            .AnyAsync(b => b.ClientId == clientId && b.ExpiresAt > DateTimeOffset.Now);
    }

    private async Task<int> UserVoteTotal(string userId)
    {
        return await _merchantsDbContext.ActiveMerchants.TagWithCallSite().Where(m => m.UploadedByUserId == userId).SumAsync(m => m.Votes);
    }

    public async Task<PushSubscription?> GetPushSubscription(string clientToken)
    {
        if (string.IsNullOrEmpty(clientToken)) return null;

        return await _merchantsDbContext.PushSubscriptions
            .TagWithCallSite()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == clientToken);
    }

    public async Task UpdatePushSubscription(PushSubscription subscription)
    {
        if (string.IsNullOrEmpty(subscription.Token)) return;

        bool exists = await _merchantsDbContext.PushSubscriptions
                        .TagWithCallSite()
                        .AnyAsync(s => s.Token == subscription.Token);
        if (exists)
        {
            _merchantsDbContext.Entry(subscription).State = EntityState.Modified;
        }
        else
        {
            _merchantsDbContext.Add(subscription);
        }
        _merchantsDbContext.SaveChanges();
    }

    public async Task RemovePushSubscription(string clientToken)
    {
        if (string.IsNullOrEmpty(clientToken)) return;

        try
        {
            var subscription = new PushSubscription()
            {
                Token = clientToken,
            };
            //Rather than delete, just purge all data from the record by storing blank values
            //If we delete, then this occasionally causes a race condition for primary/foreign key updates
            //in the background processors when pushing out notifications
            //TODO: Clean these up later in a background process at a safe time. Maybe after EF7 bulk deletes?
            _merchantsDbContext.Entry(subscription).State = EntityState.Modified;
            await _merchantsDbContext.SaveChangesAsync();
        }
        catch(DbUpdateConcurrencyException)
        {
            //If a subscription didn't exist, just ignore the error.
            //Probably happens mainly if a user multi-clicks delete before the request has completed
        }
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ProfileStats> GetProfileStats()
    {
        var votesAndCount = await _merchantsDbContext.ActiveMerchants
            .TagWithCallSite()
            .Where(m => m.UploadedByUserId == Context.UserIdentifier && m.Votes >= 0 && !m.Hidden)
            .Include(m => m.ActiveMerchantGroup)
            .GroupBy(m => m.UploadedByUserId, (_, rows) => new
            {
                VoteTotal = rows.Sum(m => m.Votes),
                TotalSubmisisons = rows.Count(),
                OldestSubmission = rows.Max(m => m.ActiveMerchantGroup.NextAppearance.Date),
                NewestSubmission = rows.Max(m => m.ActiveMerchantGroup.NextAppearance.Date),
            })
            .FirstOrDefaultAsync();

        var server = await _merchantsDbContext.ActiveMerchants
            .TagWithCallSite()
            .Where(m => m.UploadedByUserId == Context.UserIdentifier && m.Votes >= 0 && !m.Hidden)
            .Include(m => m.ActiveMerchantGroup)
            .GroupBy(m => m.ActiveMerchantGroup.Server, (server, rows) => new { 
                Server = server,
                Count = rows.Count()
            })
            .OrderByDescending(i => i.Count)
            .Select(i => i.Server)
            .FirstOrDefaultAsync();

        return new ProfileStats()
        {
             PrimaryServer = server ?? "No submissions",
             TotalUpvotes = votesAndCount?.VoteTotal ?? 0,
             UpvotedMerchats = votesAndCount?.TotalSubmisisons ?? 0,
             //NewestSubmission = votesAndCount?.NewestSubmission != null ? DateOnly.FromDateTime(votesAndCount.NewestSubmission) : null,
             //OldestSubmission = votesAndCount?.OldestSubmission != null ? DateOnly.FromDateTime(votesAndCount.OldestSubmission) : null,
        };
    }
}
