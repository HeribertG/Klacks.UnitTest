using Klacks.Api.Datas;
using Klacks.Api.Interfaces;
using Klacks.Api.Models.Associations;
using Klacks.Api.Resources.Filter;
using Microsoft.EntityFrameworkCore;

namespace UnitTest.Mocks
{
    public class MockGroupRepository : IGroupRepository
    {
        private readonly DataBaseContext context;

        public MockGroupRepository(DataBaseContext context)
        {
            this.context = context;
        }

        public async Task Add(Group model)
        {
            if (model.Parent.HasValue)
            {
                await AddChildNode(model.Parent.Value, model);
            }
            else
            {
                await AddRootNode(model);
            }
        }

        public async Task<Group?> Delete(Guid id)
        {
            var group = await context.Group.FirstOrDefaultAsync(g => g.Id == id);
            await DeleteNode(id);
            return group;
        }

        public async Task<Group> AddChildNode(Guid parentId, Group newGroup)
        {
            var parent = await context.Group
                .Where(g => g.Id == parentId && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (parent == null)
            {
                throw new KeyNotFoundException($"Eltern-Gruppe mit ID {parentId} nicht gefunden");
            }

            // Manuelle Aktualisierung der Lft/Rgt-Werte für alle betroffenen Knoten
            // Wichtig: erst die Lft-Werte aktualisieren für Knoten mit Lft > parent.rgt
            var nodesNeedLftUpdate = await context.Group
                .Where(g => g.Root == parent.Root && g.Lft > parent.rgt && !g.IsDeleted)
                .ToListAsync();

            foreach (var node in nodesNeedLftUpdate)
            {
                node.Lft += 2;
            }

            // Dann die Rgt-Werte für alle Knoten mit Rgt >= parent.rgt
            var nodesNeedRgtUpdate = await context.Group
                .Where(g => g.Root == parent.Root && g.rgt >= parent.rgt && !g.IsDeleted)
                .ToListAsync();

            foreach (var node in nodesNeedRgtUpdate)
            {
                node.rgt += 2;
            }

            await context.SaveChangesAsync();

            // Neuen Knoten einfügen
            newGroup.Lft = parent.rgt; // Der Lft-Wert ist der alte Rgt-Wert des Parents
            newGroup.rgt = parent.rgt + 1; // Der Rgt-Wert ist der Lft-Wert + 1
            newGroup.Parent = parent.Id;
            newGroup.Root = parent.Root ?? parent.Id;
            newGroup.CreateTime = DateTime.UtcNow;

            context.Group.Add(newGroup);
            await context.SaveChangesAsync();

            return newGroup;
        }

        public async Task<Group> AddRootNode(Group newGroup)
        {
            // Höchsten Rgt-Wert finden
            var maxRgt = await context.Group
                .Where(g => !g.IsDeleted && g.Root == null)
                .OrderByDescending(g => g.rgt)
                .Select(g => (int?)g.rgt)
                .FirstOrDefaultAsync() ?? 0;

            newGroup.Lft = maxRgt + 1;
            newGroup.rgt = maxRgt + 2;
            newGroup.Parent = null;
            newGroup.Root = null;
            newGroup.CreateTime = DateTime.UtcNow;

            context.Group.Add(newGroup);
            await context.SaveChangesAsync();

            return newGroup;
        }

        public async Task DeleteNode(Guid id)
        {
            var node = await context.Group
                .Where(g => g.Id == id && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (node == null)
            {
                throw new KeyNotFoundException($"Gruppe mit ID {id} nicht gefunden");
            }

            var width = node.rgt - node.Lft + 1;

            // Alle Knoten in diesem Teilbaum als gelöscht markieren
            var nodesToDelete = await context.Group
                .Where(g => g.Lft >= node.Lft && g.rgt <= node.rgt && g.Root == node.Root)
                .ToListAsync();

            foreach (var nodeToDelete in nodesToDelete)
            {
                nodeToDelete.IsDeleted = true;
                nodeToDelete.DeletedTime = DateTime.UtcNow;
                nodeToDelete.CurrentUserDeleted = "TestUser"; // Für Testzwecke
            }

            await context.SaveChangesAsync();

            // Lft und Rgt-Werte nach diesem Knoten anpassen
            var nodesRightOfDeleted = await context.Group
                .Where(g => g.Lft > node.rgt && g.Root == node.Root && !g.IsDeleted)
                .ToListAsync();

            foreach (var rightNode in nodesRightOfDeleted)
            {
                rightNode.Lft -= width;
            }

            var nodesWithRgtGreaterThanDeleted = await context.Group
                .Where(g => g.rgt > node.rgt && g.Root == node.Root && !g.IsDeleted)
                .ToListAsync();

            foreach (var rightNode in nodesWithRgtGreaterThanDeleted)
            {
                rightNode.rgt -= width;
            }

            await context.SaveChangesAsync();
        }

        public IQueryable<Group> FilterGroup(GroupFilter filter)
        {
            var tmp = context.Group.Include(gr => gr.GroupItems)
                               .ThenInclude(gi => gi.Client)
                               .AsNoTracking()
                               .OrderBy(g => g.Root)
                               .AsQueryable();

            // Weitere Filterungen aus der ursprünglichen Klasse...
            return tmp;
        }

        public async Task<Group?> Get(Guid id)
        {
            return await context.Group.Where(x => x.Id == id).Include(x => x.GroupItems).ThenInclude(x => x.Client).AsNoTracking().FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<Group>> GetChildren(Guid parentId)
        {
            var parent = await context.Group
                .Where(g => g.Id == parentId && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (parent == null)
            {
                throw new KeyNotFoundException($"Eltern-Gruppe mit ID {parentId} nicht gefunden");
            }

            return await context.Group
                .Where(g => g.Parent == parentId && !g.IsDeleted)
                .OrderBy(g => g.Lft)
                .ToListAsync();
        }

        public async Task<int> GetNodeDepth(Guid nodeId)
        {
            var node = await context.Group
                .Where(g => g.Id == nodeId && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (node == null)
            {
                throw new KeyNotFoundException($"Gruppe mit ID {nodeId} nicht gefunden");
            }

            var depth = await context.Group
                .CountAsync(g => g.Lft < node.Lft && g.rgt > node.rgt && g.Root == node.Root && !g.IsDeleted);

            return depth;
        }

        public async Task<IEnumerable<Group>> GetPath(Guid nodeId)
        {
            var node = await context.Group
                .Where(g => g.Id == nodeId && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (node == null)
            {
                throw new KeyNotFoundException($"Gruppe mit ID {nodeId} nicht gefunden");
            }

            return await context.Group
                .Where(g => g.Lft <= node.Lft && g.rgt >= node.rgt && g.Root == node.Root)
                .OrderBy(g => g.Lft)
                .ToListAsync();
        }

        public async Task<IEnumerable<Group>> GetTree(Guid? rootId = null)
        {
            if (rootId.HasValue)
            {
                var root = await context.Group
                    .Where(g => g.Id == rootId)
                    .Include(g => g.GroupItems)
                    .ThenInclude(gi => gi.Client)
                    .FirstOrDefaultAsync();

                if (root == null)
                {
                    throw new KeyNotFoundException($"Wurzel-Gruppe mit ID {rootId} nicht gefunden");
                }

                return await context.Group
                    .Where(g => g.Root == rootId)
                    .Include(g => g.GroupItems)
                    .ThenInclude(gi => gi.Client)
                    .OrderBy(g => g.Root)
                    .ThenBy(g => g.Lft)
                    .ToListAsync();
            }
            else
            {
                return await context.Group
                    .Where(g => !g.IsDeleted)
                    .Include(g => g.GroupItems)
                    .ThenInclude(gi => gi.Client)
                    .OrderBy(g => g.Root)
                    .ThenBy(g => g.Lft)
                    .ToListAsync();
            }
        }

        public async Task MoveNode(Guid nodeId, Guid newParentId)
        {
            var node = await context.Group
                .Where(g => g.Id == nodeId)
                .FirstOrDefaultAsync();

            if (node == null)
            {
                throw new KeyNotFoundException($"Zu verschiebende Gruppe mit ID {nodeId} nicht gefunden");
            }

            var newParent = await context.Group
                .Where(g => g.Id == newParentId && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (newParent == null)
            {
                throw new KeyNotFoundException($"Neue Eltern-Gruppe mit ID {newParentId} nicht gefunden");
            }

            // Prüfen, ob der neue Elternteil nicht ein Nachkomme des zu verschiebenden Knotens ist
            if (newParent.Lft > node.Lft && newParent.rgt < node.rgt)
            {
                throw new InvalidOperationException("Der neue Elternteil kann nicht ein Nachkomme des zu verschiebenden Knotens sein");
            }

            // 1. Finde alle Knoten im Teilbaum
            var subtreeNodes = await context.Group
                .Where(g => g.Lft >= node.Lft && g.rgt <= node.rgt && g.Root == node.Root)
                .ToListAsync();

            var nodeWidth = node.rgt - node.Lft + 1;

            // 2. Temporär alle Werte im Teilbaum auf negative Werte setzen
            foreach (var subtreeNode in subtreeNodes)
            {
                subtreeNode.Lft = -subtreeNode.Lft;
                subtreeNode.rgt = -subtreeNode.rgt;
            }

            await context.SaveChangesAsync();

            // 3. Lücke in der ursprünglichen Position schließen
            var nodesAfterSource = await context.Group
                .Where(g => g.Lft > node.rgt && g.Root == node.Root && !g.IsDeleted)
                .ToListAsync();

            foreach (var afterNode in nodesAfterSource)
            {
                afterNode.Lft -= nodeWidth;
            }

            var nodesRgtGreaterThanSource = await context.Group
                .Where(g => g.rgt > node.rgt && g.Root == node.Root && !g.IsDeleted)
                .ToListAsync();

            foreach (var rightNode in nodesRgtGreaterThanSource)
            {
                rightNode.rgt -= nodeWidth;
            }

            await context.SaveChangesAsync();

            // 4. Neue Position bestimmen - die Position ist der Rgt-Wert des neuen Elternteils
            int newPos = newParent.rgt;

            // Wenn die neue Position nach der alten Position liegt und 
            // bereits durch die Lückenschließung verändert wurde, muss sie angepasst werden
            if (newPos > node.rgt)
            {
                newPos -= nodeWidth;
            }

            // 5. Platz an der neuen Position schaffen
            var nodesNeedingRgtUpdate = await context.Group
                .Where(g => g.rgt >= newPos && g.Root == newParent.Root && g.Lft >= 0) // g.Lft >= 0 schließt temporär negative Knoten aus
                .ToListAsync();

            foreach (var updateNode in nodesNeedingRgtUpdate)
            {
                updateNode.rgt += nodeWidth;
            }

            var nodesNeedingLftUpdate = await context.Group
                .Where(g => g.Lft > newPos && g.Root == newParent.Root && g.Lft >= 0)
                .ToListAsync();

            foreach (var updateNode in nodesNeedingLftUpdate)
            {
                updateNode.Lft += nodeWidth;
            }

            await context.SaveChangesAsync();

            // 6. Verschiebe den Teilbaum an die neue Position
            var offset = newPos - node.Lft;

            foreach (var subtreeNode in subtreeNodes)
            {
                subtreeNode.Lft = -subtreeNode.Lft + offset;
                subtreeNode.rgt = -subtreeNode.rgt + offset;
                subtreeNode.Root = newParent.Root;
            }

            // 7. Aktualisiere den Parent-Wert des verschobenen Knotens
            node.Parent = newParentId;

            await context.SaveChangesAsync();
        }

        public async Task<Group?> Put(Group model)
        {
            var existingIds = context.GroupItem.Where(x => x.GroupId == model.Id).Select(x => x.ClientId).ToList();
            var modelListIds = model.GroupItems.Select(x => x.ClientId).ToList();

            var newIds = modelListIds.Where(x => !existingIds.Contains(x)).ToList();
            var deleteItems = existingIds.Where(x => !modelListIds.Contains(x)).ToList();

            if (newIds.Any())
            {
                var lst = CreateList(newIds, model.Id);
                context.GroupItem.AddRange(lst.ToArray());
            }

            foreach (var id in deleteItems)
            {
                var item = context.GroupItem.FirstOrDefault(x => x.ClientId == id);
                if (item != null)
                {
                    context.GroupItem.Remove(item);
                }
            }

            var existingGroup = await context.Group.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.Id);
            if (existingGroup != null)
            {
                if (existingGroup.Parent != model.Parent)
                {
                    await UpdateNode(model);

                    if (model.Parent.HasValue)
                    {
                        await MoveNode(model.Id, model.Parent.Value);
                    }
                }
                else
                {
                    await UpdateNode(model);
                }
            }

            await context.SaveChangesAsync();
            return model;
        }

        public async Task<TruncatedGroup> Truncated(GroupFilter filter)
        {
            var tmp = FilterGroup(filter);

            var count = tmp.Count();
            var maxPage = filter.NumberOfItemsPerPage > 0 ? (count / filter.NumberOfItemsPerPage) : 0;

            var firstItem = 0;

            if (count > 0 && count > filter.NumberOfItemsPerPage)
            {
                if ((filter.IsNextPage.HasValue || filter.IsPreviousPage.HasValue) && filter.FirstItemOnLastPage.HasValue)
                {
                    if (filter.IsNextPage.HasValue)
                    {
                        firstItem = filter.FirstItemOnLastPage.Value + filter.NumberOfItemsPerPage;
                    }
                    else
                    {
                        var numberOfItem = filter.NumberOfItemOnPreviousPage ?? filter.NumberOfItemsPerPage;
                        firstItem = filter.FirstItemOnLastPage.Value - numberOfItem;
                        if (firstItem < 0)
                        {
                            firstItem = 0;
                        }
                    }
                }
                else
                {
                    firstItem = filter.RequiredPage * filter.NumberOfItemsPerPage;
                }
            }
            else
            {
                firstItem = filter.RequiredPage * filter.NumberOfItemsPerPage;
            }

            tmp = tmp.Skip(firstItem).Take(filter.NumberOfItemsPerPage);

            var groups = count == 0 ? new List<Group>() : await tmp.ToListAsync();
            var res = new TruncatedGroup
            {
                Groups = groups,
                MaxItems = count,
            };

            if (filter.NumberOfItemsPerPage > 0)
            {
                res.MaxPages = count % filter.NumberOfItemsPerPage == 0 ? maxPage - 1 : maxPage;
            }

            res.CurrentPage = filter.RequiredPage;
            res.FirstItemOnPage = res.MaxItems <= firstItem ? -1 : firstItem;

            return res;
        }

        public async Task UpdateNode(Group updatedGroup)
        {
            var existingGroup = await context.Group
                .Where(g => g.Id == updatedGroup.Id && !g.IsDeleted)
                .FirstOrDefaultAsync();

            if (existingGroup == null)
            {
                throw new KeyNotFoundException($"Gruppe mit ID {updatedGroup.Id} nicht gefunden");
            }

            // Nur die grundlegenden Informationen aktualisieren, nicht die Baumstruktur
            existingGroup.Name = updatedGroup.Name;
            existingGroup.Description = updatedGroup.Description;
            existingGroup.ValidFrom = updatedGroup.ValidFrom;
            existingGroup.ValidUntil = updatedGroup.ValidUntil;
            existingGroup.UpdateTime = DateTime.UtcNow;
            existingGroup.CurrentUserUpdated = updatedGroup.CurrentUserUpdated;

            context.Group.Update(existingGroup);
            await context.SaveChangesAsync();
        }

        private List<GroupItem> CreateList(List<Guid> list, Guid groubId)
        {
            var lst = new List<GroupItem>();
            foreach (var id in list)
            {
                lst.Add(new GroupItem() { ClientId = id, GroupId = groubId });
            }

            return lst;
        }

        public Task<bool> Exists(Guid id)
        {
            throw new NotImplementedException();
        }

        public Task<List<Group>> List()
        {
            throw new NotImplementedException();
        }

        public void Remove(Group model)
        {
            throw new NotImplementedException();
        }
    }
}