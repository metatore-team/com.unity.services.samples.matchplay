using System;
using System.Linq;
using System.Threading.Tasks;
using Matchplay.Shared;
using UnityEngine;
using Unity.Services.Matchmaker;
using Unity.Services.Matchmaker.Models;

namespace Matchplay.Server
{
    public class MatchplayBackfiller : IDisposable
    {
        public bool Backfilling { get; private set; } = false;

        CreateBackfillTicketOptions m_CreateBackfillOptions;
        BackfillTicket m_LocalBackfillData;
        bool m_LocalDataDirty = false;
        const int k_TicketCheckMs = 1000;
        int m_MaxPlayers;
        int MatchPlayerCount => m_LocalBackfillData?.Properties.MatchProperties.Players.Count ?? 0;

        public MatchplayBackfiller(string connection, string queueName, MatchProperties matchmakerPayloadProperties, int maxPlayers)
        {
            m_MaxPlayers = maxPlayers;
            var backfillProperties = new BackfillTicketProperties(matchmakerPayloadProperties);
            m_LocalBackfillData = new BackfillTicket { Id = matchmakerPayloadProperties.BackfillTicketId, Properties = backfillProperties };

            m_CreateBackfillOptions = new CreateBackfillTicketOptions
            {
                Connection = connection,
                QueueName = queueName,
                Properties = backfillProperties
            };
        }

        public async Task BeginBackfilling()
        {
           // SetStagingEnvironment(); for internal unity testing only.

            if (Backfilling)
            {
                Debug.LogWarning("Already backfilling, no need to start another.");
                return;
            }

            Debug.Log($"Starting backfill  Server: {MatchPlayerCount}/{m_MaxPlayers}");

            //Create a ticket if we don't have one already (via Allocation)
            if (string.IsNullOrEmpty(m_LocalBackfillData.Id))
                m_LocalBackfillData.Id = await MatchmakerService.Instance.CreateBackfillTicketAsync(m_CreateBackfillOptions);

            Backfilling = true;

            //we want to create an asynchronous backfill loop.
#pragma warning disable 4014
            BackfillLoop();
#pragma warning restore 4014
        }

        /// <summary>
        /// The matchmaker maintains the state of the match on the backend.
        /// As such we just need to manually handle cases where the player's join/leave outside of the service.
        /// </summary>
        public void AddPlayerToMatch(UserData userData)
        {
            if (!Backfilling)
            {
                Debug.LogWarning("Can't add users to the backfill ticket before it's been created");
                return;
            }

            if (GetPlayerById(userData.userAuthId) == null)
            {
                Debug.LogWarning($"User: {userData.userName} - {userData.userAuthId} already in Match. Ignoring add.");
                return;
            }

            var matchmakerPlayer = new Player(userData.userAuthId, userData.userGamePreferences);

            m_LocalBackfillData.Properties.MatchProperties.Players.Add(matchmakerPlayer);

            m_LocalDataDirty = true;
        }

        public void RemovePlayerFromMatch(string userID)
        {
            var playerToRemove = GetPlayerById(userID);
            if (playerToRemove == null)
            {
                Debug.LogWarning($"No user by the ID: {userID} in local backfill Data.");
                return;
            }

            m_LocalBackfillData.Properties.MatchProperties.Players.Remove(playerToRemove);

            //We Only have one team in this game, so this simplifies things here
            m_LocalBackfillData.Properties.MatchProperties.Teams[0].PlayerIds.Remove(userID);
            m_LocalDataDirty = true;
        }

        public async Task StopBackfill()
        {
            if (!Backfilling)
            {
                Debug.LogError("Can't stop backfilling before we start.");
                return;
            }

            await MatchmakerService.Instance.DeleteBackfillTicketAsync(m_LocalBackfillData.Id);
            Backfilling = false;
            m_LocalBackfillData.Id = null;
        }

        public bool NeedsPlayers()
        {
            return MatchPlayerCount < m_MaxPlayers;
        }


        /// <summary>
        /// Internal use Only TODO Remove before shipping
        /// </summary>
        void SetStagingEnvironment()
        {
            var sdkConfiguration = (IMatchmakerSdkConfiguration)MatchmakerService.Instance;
            sdkConfiguration.SetBasePath("https://matchmaker-stg.services.api.unity.com");
            Debug.LogWarning("CAUTION: Setting Backfill environment to Staging!");
        }

        Player GetPlayerById(string userID)
        {
            return m_LocalBackfillData.Properties.MatchProperties.Players.FirstOrDefault(p => p.Id.Equals(userID));
        }

        /// <summary>
        /// Generally it's a good idea to get the latest state of the backfill before modifying it and updating
        /// </summary>
        async Task BackfillLoop()
        {
            while (Backfilling)
            {
                m_LocalBackfillData = await MatchmakerService.Instance.ApproveBackfillTicketAsync(m_LocalBackfillData.Id);

                if (!NeedsPlayers())
                {
                    await StopBackfill();
                    break;
                }

                if (m_LocalDataDirty)
                {
                    await MatchmakerService.Instance.UpdateBackfillTicketAsync(m_LocalBackfillData.Id, m_LocalBackfillData);
                    m_LocalDataDirty = false;
                }

                //Backfill Docs reccommend a once-per-second approval for backfill tickets
                await Task.Delay(k_TicketCheckMs);
            }
        }

        public void Dispose()
        {
#pragma warning disable 4014
            StopBackfill();
#pragma warning restore 4014
        }
    }
}
