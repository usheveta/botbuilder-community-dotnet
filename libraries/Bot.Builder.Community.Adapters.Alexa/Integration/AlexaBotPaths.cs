﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Bot.Builder.Community.Adapters.Alexa.Integration
{
    public class AlexaBotPaths
    {
        public AlexaBotPaths()
        {
            this.BasePath = "/api";
            this.SkillRequestsPath = "skillrequests";
        }

        public string BasePath { get; set; }
        public string SkillRequestsPath { get; set; }
    }
}