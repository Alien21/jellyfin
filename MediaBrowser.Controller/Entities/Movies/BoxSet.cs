#nullable disable

#pragma warning disable CA1721, CA1819, CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace MediaBrowser.Controller.Entities.Movies
{
    /// <summary>
    /// Class BoxSet.
    /// </summary>
    public class BoxSet : Folder, IHasTrailers, IHasDisplayOrder, IHasLookupInfo<BoxSetInfo>
    {
        public BoxSet()
        {
            DisplayOrder = "PremiereDate";
        }

        [JsonIgnore]
        protected override bool FilterLinkedChildrenPerUser => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the display order.
        /// </summary>
        /// <value>The display order.</value>
        public string DisplayOrder { get; set; }

        [JsonIgnore]
        private bool IsLegacyBoxSet
        {
            get
            {
                if (string.IsNullOrEmpty(Path))
                {
                    return false;
                }

                if (LinkedChildren.Length > 0)
                {
                    return false;
                }

                return !FileSystem.ContainsSubPath(ConfigurationManager.ApplicationPaths.DataPath, Path);
            }
        }

        [JsonIgnore]
        public override bool IsPreSorted => true;

        public Guid[] LibraryFolderIds { get; set; }

        protected override bool GetBlockUnratedValue(User user)
        {
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Movie);
        }

        public override double GetDefaultPrimaryImageAspectRatio()
            => 2.0 / 3;

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Movie;
        }

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService)
        {
            if (IsLegacyBoxSet)
            {
                return base.GetNonCachedChildren(directoryService);
            }

            return [];
        }

        protected override IReadOnlyList<BaseItem> LoadChildren()
        {
            if (IsLegacyBoxSet)
            {
                return base.LoadChildren();
            }

            // Save a trip to the database
            return [];
        }

        public override bool IsAuthorizedToDelete(User user, List<Folder> allCollectionFolders)
        {
            return user.HasPermission(PermissionKind.IsAdministrator) || user.HasPermission(PermissionKind.EnableCollectionManagement);
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        private IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User user)
        {
            if (!Enum.TryParse<ItemSortBy>(DisplayOrder, out var sortBy))
            {
                sortBy = ItemSortBy.PremiereDate;
            }

            if (sortBy == ItemSortBy.Default)
            {
                return items;
            }

            return LibraryManager.Sort(items, user, new[] { sortBy }, SortOrder.Ascending);
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            var children = base.GetChildren(user, includeLinkedChildren, query);
            return Sort(children, user).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query = null)
        {
            var children = base.GetChildren(user, includeLinkedChildren, out totalItemCount, query);
            return Sort(children, user).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount)
        {
            var children = base.GetRecursiveChildren(user, query, out totalCount);
            return Sort(children, user).ToArray();
        }

        public BoxSetInfo GetLookupInfo()
        {
            return GetItemLookupInfo<BoxSetInfo>();
        }

        public int GetUniqueItemCount(User user)
        {
            return GetUniqueItemCount(GetChildren(user, true, null));
        }

        /// <summary>
        /// Counts distinct collection members, matching library copies by shared metadata identifiers.
        /// </summary>
        /// <param name="children">The collection members.</param>
        /// <returns>The number of distinct members.</returns>
        public static int GetUniqueItemCount(IReadOnlyList<BaseItem> children)
        {
            return GetUniqueItemGroups(children).Count;
        }

        public override void FillUserDataDtoValues(
            UserItemDataDto dto,
            UserItemData userData,
            BaseItemDto itemDto,
            User user,
            DtoOptions fields,
            (int Played, int Total)? precomputedCounts = null)
        {
            base.FillUserDataDtoValues(dto, userData, itemDto, user, fields, precomputedCounts);
            if (!ConfigurationManager.Configuration.EnableLibrarySourceDetails)
            {
                return;
            }

            var children = GetChildren(user, true, null);
            var groups = GetUniqueItemGroups(children);
            if (itemDto is not null)
            {
                itemDto.UniqueItemCount = groups.Count;
            }

            // Mixed collections can contain series or nested collections: their play state still
            // describes recursive playable descendants rather than the direct collection members.
            if (children.Any(child => child is not Movie and not Episode))
            {
                return;
            }

            var userDataBatch = UserDataManager.GetUserDataBatch(children, user);
            var played = groups.Count(group => group.Any(child =>
                userDataBatch.TryGetValue(child.Id, out var data) && data.Played));
            dto.UnplayedItemCount = groups.Count - played;
            dto.PlayedPercentage = groups.Count > 0 ? played / (double)groups.Count * 100 : 0;
            dto.Played = played == groups.Count;
        }

        private static List<List<BaseItem>> GetUniqueItemGroups(IReadOnlyList<BaseItem> children)
        {
            var parents = Enumerable.Range(0, children.Count).ToArray();
            var identities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int Find(int index)
            {
                while (parents[index] != index)
                {
                    parents[index] = parents[parents[index]];
                    index = parents[index];
                }

                return index;
            }

            void AddIdentity(string identity, int index)
            {
                if (identities.TryGetValue(identity, out var previous))
                {
                    parents[Find(index)] = Find(previous);
                }
                else
                {
                    identities[identity] = index;
                }
            }

            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                AddIdentity("item:" + child.Id, index);
                var itemKind = child switch
                {
                    Movie => "Movie",
                    Series => "Series",
                    Episode => "Episode",
                    _ => null
                };
                if (itemKind is null)
                {
                    continue;
                }

                var providers = child is Movie
                    ? new[] { MetadataProvider.Tmdb, MetadataProvider.Imdb }
                    : new[] { MetadataProvider.Tmdb, MetadataProvider.Imdb, MetadataProvider.Tvdb };
                foreach (var provider in providers)
                {
                    var providerId = child.GetProviderId(provider);
                    if (!string.IsNullOrWhiteSpace(providerId))
                    {
                        AddIdentity(itemKind + ":" + provider + ":" + providerId.Trim(), index);
                    }
                }
            }

            return children.Select((child, index) => (Child: child, Group: Find(index)))
                .GroupBy(pair => pair.Group, pair => pair.Child)
                .Select(group => group.ToList())
                .ToList();
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false)
        {
            if (IsLegacyBoxSet)
            {
                return base.IsVisible(user, skipAllowedTagsCheck);
            }

            if (!IsParentalAllowed(user, skipAllowedTagsCheck))
            {
                return false;
            }

            if (LinkedChildren.Length == 0)
            {
                return true;
            }

            List<BaseItem> linkedItems = null;
            var libraryFolderIds = LibraryFolderIds;
            if (libraryFolderIds is null)
            {
                linkedItems = GetLinkedChildren(DtoOptions.StoredColumnsOnly);
                libraryFolderIds = GetLibraryFolderIds(linkedItems);
            }

            if (libraryFolderIds.Length == 0)
            {
                return true;
            }

            var userLibraryFolderIds = GetLibraryFolderIds(user);

            if (!userLibraryFolderIds.Any(i => libraryFolderIds.Contains(i)))
            {
                return false;
            }

            // If user has parental controls, hide the BoxSet when all children are restricted
            if (user.MaxParentalRatingScore.HasValue)
            {
                linkedItems ??= GetLinkedChildren(DtoOptions.StoredColumnsOnly);
                if (linkedItems.Count > 0 && linkedItems.All(child => !child.IsParentalAllowed(user, true)))
                {
                    return false;
                }
            }

            return true;
        }

        public override void MarkPlayed(User user, DateTime? datePlayed, bool resetPosition)
        {
            if (IsLegacyBoxSet)
            {
                base.MarkPlayed(user, datePlayed, resetPosition);
                return;
            }

            foreach (var item in GetLinkedChildren(user))
            {
                item.MarkPlayed(user, datePlayed, resetPosition);
            }
        }

        public override void MarkUnplayed(User user)
        {
            if (IsLegacyBoxSet)
            {
                base.MarkUnplayed(user);
                return;
            }

            foreach (var item in GetLinkedChildren(user))
            {
                item.MarkUnplayed(user);
            }
        }

        public override bool IsVisibleStandalone(User user)
        {
            if (IsLegacyBoxSet)
            {
                return base.IsVisibleStandalone(user);
            }

            return IsVisible(user);
        }

        private Guid[] GetLibraryFolderIds(User user)
        {
            return LibraryManager.GetUserRootFolder().GetChildren(user, true)
                .Select(i => i.Id)
                .ToArray();
        }

        public Guid[] GetLibraryFolderIds()
        {
            return GetLibraryFolderIds(GetLinkedChildren(DtoOptions.StoredColumnsOnly));
        }

        private Guid[] GetLibraryFolderIds(IEnumerable<BaseItem> linkedChildren)
        {
            // Seeded with this box set so a cycle through a nested collection terminates.
            var expandedFolders = new List<Guid> { Id };

            // The user root children are the same for every item.
            var rootChildren = LibraryManager.GetUserRootFolder().Children.OfType<Folder>().ToList();

            return FlattenItems(linkedChildren, expandedFolders)
                .SelectMany(i => LibraryManager.GetCollectionFolders(i, rootChildren))
                .Select(i => i.Id)
                .Distinct()
                .ToArray();
        }

        private IEnumerable<BaseItem> FlattenItems(IEnumerable<BaseItem> items, List<Guid> expandedFolders)
        {
            return items
                .SelectMany(i => FlattenItems(i, expandedFolders));
        }

        private IEnumerable<BaseItem> FlattenItems(BaseItem item, List<Guid> expandedFolders)
        {
            if (item is BoxSet boxset)
            {
                if (!expandedFolders.Contains(item.Id))
                {
                    expandedFolders.Add(item.Id);

                    return FlattenItems(boxset.GetLinkedChildren(DtoOptions.StoredColumnsOnly), expandedFolders);
                }

                return [];
            }

            return [item];
        }
    }
}
