﻿// This file is a part of Music Turret project.
// 
// Copyright 2018 Emzi0767
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.Lavalink;
using DSharpPlus.Lavalink.EventArgs;
using Emzi0767.MusicTurret.Services;
using Newtonsoft.Json;

namespace Emzi0767.MusicTurret.Data
{
    /// <summary>
    /// Represents data for the music playback in a discord guild.
    /// </summary>
    public sealed class GuildMusicData : IIdentifiable
    {
        /// <summary>
        /// Gets the guild ID for this dataset.
        /// </summary>
        public string Identifier { get; }

        /// <summary>
        /// Gets the repeat mode set for this guild.
        /// </summary>
        public RepeatMode RepeatMode { get; private set; } = RepeatMode.None;

        /// <summary>
        /// Gets whether the queue for this guild is shuffled.
        /// </summary>
        public bool IsShuffled { get; private set; } = false;

        /// <summary>
        /// Gets whether a track is currently playing.
        /// </summary>
        public bool IsPlaying { get; private set; } = false;

        /// <summary>
        /// Gets the playback volume for this guild.
        /// </summary>
        public int Volume { get; private set; } = 100;

        /// <summary>
        /// Gets the current music queue.
        /// </summary>
        public IReadOnlyCollection<MusicItem> Queue { get; }

        /// <summary>
        /// Gets the currently playing item.
        /// </summary>
        public MusicItem NowPlaying { get; private set; } = default;

        /// <summary>
        /// Gets the channel in which the music is played.
        /// </summary>
        public DiscordChannel Channel => this.Player?.Channel;

        /// <summary>
        /// Gets or sets the channel in which commands are executed.
        /// </summary>
        public DiscordChannel CommandChannel { get; set; }

        private List<MusicItem> QueueInternal { get; }
        private SemaphoreSlim QueueInternalLock { get; }
        private string QueueSerialized { get; set; }
        private DiscordGuild Guild { get; }
        private CSPRNG RNG { get; }
        private LavalinkService Lavalink { get; }
        private LavalinkGuildConnection Player { get; set; }
        private RedisClient Redis { get; } 

        /// <summary>
        /// Creates a new instance of playback data.
        /// </summary>
        /// <param name="guild">Guild to track data for.</param>
        /// <param name="rng">Cryptographically-secure random number generator implementation.</param>
        /// <param name="lavalink">Lavalink service.</param>
        /// <param name="redis">Redis service.</param>
        public GuildMusicData(DiscordGuild guild, CSPRNG rng, LavalinkService lavalink, RedisClient redis)
        {
            this.Guild = guild;
            this.RNG = rng;
            this.Lavalink = lavalink;
            this.Redis = redis;
            this.Identifier = this.Guild.Id.ToString(CultureInfo.InvariantCulture);
            this.QueueInternalLock = new SemaphoreSlim(1, 1);
            this.QueueInternal = new List<MusicItem>();
            this.Queue = new ReadOnlyCollection<MusicItem>(this.QueueInternal);
        }

        /// <summary>
        /// Asynchronously saves data from this data tracker to specified redis instance.
        /// </summary>
        /// <returns></returns>
        internal async Task SaveAsync()
        {
            lock (this.QueueInternal)
            {
                this.QueueSerialized = JsonConvert.SerializeObject(this.QueueInternal.Select(x => new MusicItemSerializable(x)));
            }

            await this.Redis.SetValueForAsync(this, x => x.RepeatMode);
            await this.Redis.SetValueForAsync(this, x => x.IsShuffled);
            await this.Redis.SetValueForAsync(this, x => x.Volume);
            await this.Redis.SetValueForAsync(this, x => x.QueueSerialized);

            this.QueueSerialized = null;
        }

        /// <summary>
        /// Asynchronously loads data for this data tracker from specified redis instance.
        /// </summary>
        /// <returns></returns>
        internal async Task LoadAsync()
        {
            await this.Redis.GetValueForAsync(this, x => x.RepeatMode, this.RepeatMode);
            await this.Redis.GetValueForAsync(this, x => x.IsShuffled, this.IsShuffled);
            await this.Redis.GetValueForAsync(this, x => x.Volume, this.Volume);
            await this.Redis.GetValueForAsync(this, x => x.QueueSerialized, "[]");

            var rawQueue = JsonConvert.DeserializeObject<IEnumerable<MusicItemSerializable>>(this.QueueSerialized);
            this.QueueSerialized = null;

            var mbrs = await Task.WhenAll(rawQueue.Select(x => x.MemberId).Distinct().Select(x => this.Guild.GetMemberAsync(x)));
            var members = mbrs.ToDictionary(x => x.Id, x => x);

            lock (this.QueueInternal)
            {
                this.QueueInternal.Clear();
                foreach (var rawItem in rawQueue)
                    this.QueueInternal.Add(new MusicItem(LavalinkUtilities.DecodeTrack(rawItem.Track), members[rawItem.MemberId]));
            }
        }

        /// <summary>
        /// Begins playback.
        /// </summary>
        public async Task PlayAsync()
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            if (this.NowPlaying.Track?.TrackString == null)
                await this.PlayHandlerAsync();
        }

        /// <summary>
        /// Stops the playback.
        /// </summary>
        public async Task StopAsync()
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            this.NowPlaying = default;
            await this.Player.StopAsync();
        }

        /// <summary>
        /// Pauses the playback.
        /// </summary>
        public async Task PauseAsync()
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            this.IsPlaying = false;
            await this.Player.PauseAsync();
        }

        /// <summary>
        /// Resumes the playback.
        /// </summary>
        public async Task ResumeAsync()
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            this.IsPlaying = true;
            await this.Player.ResumeAsync();
        }

        /// <summary>
        /// Sets playback volume.
        /// </summary>
        public async Task SetVolumeAsync(int volume)
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            await this.Player.SetVolumeAsync(volume);
            this.Volume = volume;
        }

        /// <summary>
        /// Restarts current track.
        /// </summary>
        public async Task RestartAsync()
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            if (this.NowPlaying.Track.TrackString == null)
                return;

            await this.QueueInternalLock.WaitAsync();
            try
            {
                this.QueueInternal.Insert(0, this.NowPlaying);
                await this.Player.StopAsync();
            }
            finally
            {
                this.QueueInternalLock.Release();
            }
        }

        /// <summary>
        /// Seeks the currently-playing track.
        /// </summary>
        /// <param name="target">Where or how much to seek by.</param>
        /// <param name="relative">Whether the seek is relative.</param>
        public async Task SeekAsync(TimeSpan target, bool relative)
        {
            if (this.Player == null || !this.Player.IsConnected)
                return;

            if (!relative)
                await this.Player.SeekAsync(target);
            else
                await this.Player.SeekAsync(this.Player.CurrentState.PlaybackPosition + target);
        }

        /// <summary>
        /// Empties the playback queue.
        /// </summary>
        /// <returns>Number of cleared items.</returns>
        public int EmptyQueue()
        {
            lock (this.QueueInternal)
            {
                var itemCount = this.QueueInternal.Count;
                this.QueueInternal.Clear();
                return itemCount;
            }
        }

        /// <summary>
        /// Shuffles the playback queue.
        /// </summary>
        public void Shuffle()
        {
            if (this.IsShuffled)
                return;

            this.IsShuffled = true;
            this.Reshuffle();
        }

        /// <summary>
        /// Reshuffles the playback queue.
        /// </summary>
        public void Reshuffle()
        {
            lock (this.QueueInternal)
            {
                this.QueueInternal.Sort(new Shuffler<MusicItem>(this.RNG));
            }
        }

        /// <summary>
        /// Causes the queue to no longer be shuffled.
        /// </summary>
        public void StopShuffle()
        {
            this.IsShuffled = false;
        }

        /// <summary>
        /// Sets the queue's repeat mode.
        /// </summary>
        public void SetRepeatMode(RepeatMode mode)
        {
            var pMode = this.RepeatMode;
            this.RepeatMode = mode;

            if (this.NowPlaying.Track.TrackString != null)
            {
                if (mode == RepeatMode.Single && mode != pMode)
                {
                    lock (this.QueueInternal)
                    {
                        this.QueueInternal.Insert(0, this.NowPlaying);
                    }
                }
                else if (mode != RepeatMode.Single && pMode == RepeatMode.Single)
                {
                    lock (this.QueueInternal)
                    {
                        this.QueueInternal.RemoveAt(0);
                    }
                }
            }
        }

        /// <summary>
        /// Enqueues a music track for playback.
        /// </summary>
        /// <param name="item">Music track to enqueue.</param>
        public void Enqueue(MusicItem item)
        {
            lock (this.QueueInternal)
            {
                if (this.RepeatMode == RepeatMode.All && this.QueueInternal.Count == 1)
                {
                    this.QueueInternal.Insert(0, item);
                }
                else if (!this.IsShuffled || !this.QueueInternal.Any())
                {
                    this.QueueInternal.Add(item);
                }
                else if (this.IsShuffled)
                {
                    var index = this.RNG.Next(0, this.QueueInternal.Count);
                    this.QueueInternal.Insert(index, item);
                }
            }
        }

        /// <summary>
        /// Dequeues next music item for playback.
        /// </summary>
        /// <returns>Dequeued item, or null if dequeueing fails.</returns>
        public MusicItem? Dequeue()
        {
            lock (this.QueueInternal)
            {
                if (this.QueueInternal.Count == 0)
                    return null;

                if (this.RepeatMode == RepeatMode.None)
                {
                    var item = this.QueueInternal[0];
                    this.QueueInternal.RemoveAt(0);
                    return item;
                }

                if (this.RepeatMode == RepeatMode.Single)
                {
                    var item = this.QueueInternal[0];
                    return item;
                }

                if (this.RepeatMode == RepeatMode.All)
                {
                    var item = this.QueueInternal[0];
                    this.QueueInternal.RemoveAt(0);
                    this.QueueInternal.Add(item);
                    return item;
                }
            }

            return null;
        }

        /// <summary>
        /// Removes a track from the queue.
        /// </summary>
        /// <param name="index">Index of the track to remove.</param>
        public MusicItem? Remove(int index)
        {
            lock (this.QueueInternal)
            {
                if (index < 0 || index >= this.QueueInternal.Count)
                    return null;

                var item = this.QueueInternal[index];
                this.QueueInternal.RemoveAt(index);
                return item;
            }
        }

        /// <summary>
        /// Creates a player for this guild.
        /// </summary>
        /// <returns></returns>
        public async Task CreatePlayerAsync(DiscordChannel channel)
        {
            if (this.Player != null && this.Player.IsConnected)
                return;

            this.Player = await this.Lavalink.LavalinkNode.ConnectAsync(channel);
            if (this.Volume != 100)
                await this.Player.SetVolumeAsync(this.Volume);
            this.Player.PlaybackFinished += this.Player_PlaybackFinished;
        }

        /// <summary>
        /// Destroys a player for this guild.
        /// </summary>
        /// <returns></returns>
        public async Task DestroyPlayerAsync()
        {
            if (this.Player == null)
                return;

            if (this.Player.IsConnected)
                await this.Player.DisconnectAsync();

            this.Player = null;
            await this.SaveAsync();
        }

        /// <summary>
        /// Gets the current position in the track.
        /// </summary>
        /// <returns>Position in the track.</returns>
        public TimeSpan GetCurrentPosition()
        {
            if (this.NowPlaying.Track.TrackString == null)
                return TimeSpan.Zero;

            return this.Player.CurrentState.PlaybackPosition;
        }

        private async Task Player_PlaybackFinished(TrackFinishEventArgs e)
        {
            await Task.Delay(500);
            this.IsPlaying = false;
            await this.PlayHandlerAsync();

            await this.SaveAsync();
        }

        private async Task PlayHandlerAsync()
        {
            var itemN = this.Dequeue();
            if (itemN == null)
            {
                this.NowPlaying = default;
                return;
            }

            var item = itemN.Value;
            this.NowPlaying = item;
            this.IsPlaying = true;
            await this.Player.PlayAsync(item.Track);
        }
    }
}
