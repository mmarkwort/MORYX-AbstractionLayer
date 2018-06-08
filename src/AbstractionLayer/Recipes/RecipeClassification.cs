﻿using System;

namespace Marvin.AbstractionLayer
{
    /// <summary>
    /// Classification of recipes as a Bitflag. Bits 0 -> 30 are free for recipe defintions. Bit 31 indicates a <see cref="Clone"/>.
    /// <see cref="CloneFilter"/> is represented as 0111...1111 to filter and revert clones. 
    /// Using binary AND and OR can convert and filter clones.
    /// </summary>
    [Flags]
    public enum RecipeClassification
    {
        /// <summary>
        /// Recipe is a default for the production of the product
        /// </summary>
        Default = 1,

        /// <summary>
        /// Recipe is an alternative for the production of the product
        /// </summary>
        Alternative = 1 << 4,

        /// <summary>
        /// Recipe produces an intermediate result for the product
        /// </summary>
        Intermediate = 1 << 8,

        /// <summary>
        /// Recipe only produces part of the final product
        /// </summary>
        Part = 1 << 12,

        /// <summary>
        /// Classification that can filter clone in binary AND
        /// </summary>
        CloneFilter = int.MaxValue, 

        /// <summary>
        /// Recipe is a clone of any of the previous types
        /// </summary>
        Clone = int.MinValue, 
    }
}