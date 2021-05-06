﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;

namespace Microsoft.TemplateEngine.Abstractions
{
    public interface IPostActionLocalizationModel
    {
        /// <summary>
        /// Gets the localized description of this post action.
        /// </summary>
        string? Description { get; }

        /// <summary>
        /// Gets the localized manual instructions that the user should perform.
        /// The key represents the instruction id.
        /// </summary>
        IReadOnlyDictionary<string, string> Instructions { get; }
    }
}
