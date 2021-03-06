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

namespace Emzi0767.MusicTurret.Data
{
    /// <summary>
    /// Represents kind of an entity with associated ID.
    /// </summary>
    public enum DatabaseEntityKind
    {
        /// <summary>
        /// Defines that the entity is a user.
        /// </summary>
        User,

        /// <summary>
        /// Defines that the entity is a channel.
        /// </summary>
        Channel,

        /// <summary>
        /// Defines that the entity is a guild.
        /// </summary>
        Guild
    }
}
