﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using LogicAndTrick.Oy;
using Sledge.BspEditor.Modification;
using Sledge.BspEditor.Modification.Operations.Selection;
using Sledge.BspEditor.Modification.Operations.Tree;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Rendering.Viewport;
using Sledge.BspEditor.Tools.Properties;
using Sledge.Common;
using Sledge.Common.Shell.Components;
using Sledge.Common.Shell.Context;
using Sledge.Common.Shell.Hotkeys;
using Sledge.Common.Shell.Settings;
using Sledge.Common.Translations;
using Sledge.DataStructures.GameData;
using Sledge.DataStructures.Geometric;
using Sledge.Rendering.Cameras;
using Sledge.Rendering.Pipelines;
using Sledge.Rendering.Primitives;
using Sledge.Rendering.Resources;

namespace Sledge.BspEditor.Tools.Entity
{
    [Export(typeof(ITool))]
    [Export(typeof(ISettingsContainer))]
    [OrderHint("F")]
    [AutoTranslate]
    [DefaultHotkey("Shift+E")]
    public class EntityTool : BaseTool, ISettingsContainer
    {
        private enum EntityState
        {
            None,
            Drawn,
            Moving
        }

        private Vector3 _location;
        private EntityState _state;
        private string _activeEntity;

        public string CreateObject { get; set; } = "Create {0}";

        // Settings

        [Setting("SelectCreatedEntity")] private bool _selectCreatedEntity = true;
        [Setting("SwitchToSelectAfterCreation")] private bool _switchToSelectAfterCreation = false;
        [Setting("ResetEntityTypeOnCreation")] private bool _resetEntityTypeOnCreation = false;

        string ISettingsContainer.Name => "Sledge.BspEditor.Tools.EntityTool";

        IEnumerable<SettingKey> ISettingsContainer.GetKeys()
        {
            yield return new SettingKey("Tools/Entity", "SelectCreatedEntity", typeof(bool));
            yield return new SettingKey("Tools/Entity", "SwitchToSelectAfterCreation", typeof(bool));
            yield return new SettingKey("Tools/Entity", "ResetEntityTypeOnCreation", typeof(bool));
        }

        void ISettingsContainer.LoadValues(ISettingsStore store)
        {
            store.LoadInstance(this);
        }

        void ISettingsContainer.StoreValues(ISettingsStore store)
        {
            store.StoreInstance(this);
        }

        public EntityTool()
        {
            Usage = ToolUsage.Both;
            _location = new Vector3(0, 0, 0);
            _state = EntityState.None;
        }

        protected override void ContextChanged(IContext context)
        {
            _activeEntity = context.Get<string>("EntityTool:ActiveEntity");
            Invalidate();

            base.ContextChanged(context);
        }

        protected override void DocumentChanged()
        {
            Task.Factory.StartNew(BuildMenu);
            base.DocumentChanged();
        }

        private ToolStripItem[] _menu;

        private async void BuildMenu()
        {
            _menu = null;
            if (Document == null) return;

            var gd = await Document.Environment.GetGameData();
            if (gd == null) return;

            var items = new List<ToolStripItem>();
            var classes = gd.Classes.Where(x => x.ClassType != ClassType.Base && x.ClassType != ClassType.Solid).ToList();
            var groups = classes.GroupBy(x => x.Name.Split('_')[0]);
            foreach (var g in groups)
            {
                var mi = new ToolStripMenuItem(g.Key);
                var l = g.ToList();
                if (l.Count == 1)
                {
                    var cls = l[0];
                    mi.Text = cls.Name;
                    mi.Tag = cls;
                    mi.Click += (s, e) => CreateEntity(_location, cls.Name);
                }
                else
                {
                    var subs = l.Select(x =>
                    {
                        var item = new ToolStripMenuItem(x.Name) { Tag = x };
                        item.Click += (s, e) => CreateEntity(_location, x.Name);
                        return item;
                    }).OfType<ToolStripItem>().ToArray();
                    mi.DropDownItems.AddRange(subs);
                }
                items.Add(mi);
            }
            _menu = items.ToArray();
        }

        public override Image GetIcon()
        {
            return Resources.Tool_Entity;
        }

        public override string GetName()
        {
            return "Entity Tool";
        }

        protected override IEnumerable<Subscription> Subscribe()
        {
            yield return Oy.Subscribe<RightClickMenuBuilder>("MapViewport:RightClick", b =>
            {
                b.Clear();
                b.AddCallback(String.Format(CreateObject, _activeEntity), () => CreateEntity(_location));

                if (_menu == null || _menu.Length <= 0) return;

                b.AddSeparator();
                b.Add(_menu);
            });
        }

        // 3D interaction

        private Vector3? GetIntersectionPoint(IMapObject obj, DataStructures.Geometric.Line line)
        {
            // todo BETA !selection opacity/hidden
            //.Where(x => x.Opacity > 0 && !x.IsHidden)
            return obj?.GetPolygons()
                .Select(x => x.GetIntersectionPoint(line))
                .Where(x => x != null)
                .OrderBy(x => (x.Value - line.Start).Length())
                .FirstOrDefault();
        }

        private IEnumerable<IMapObject> GetBoundingBoxIntersections(DataStructures.Geometric.Line ray)
        {
            return Document.Map.Root.Collect(
                x => x is Root || (x.BoundingBox != null && x.BoundingBox.IntersectsWith(ray)),
                x => x.Hierarchy.Parent != null && !x.Hierarchy.HasChildren
            );
        }

        protected override void MouseDown(MapViewport viewport, PerspectiveCamera camera, ViewportEvent e)
        {
            if (e.Button != MouseButtons.Left) return;

            // Get the ray that is cast from the clicked point along the viewport frustrum
            var (rs, re) = camera.CastRayFromScreen(new Vector3(e.X, e.Y, 0));
            var ray = new Line(rs, re);

            // Grab all the elements that intersect with the ray
            var hits = GetBoundingBoxIntersections(ray);

            // Sort the list of intersecting elements by distance from ray origin and grab the first hit
            var hit = hits
                .Select(x => new { Item = x, Intersection = GetIntersectionPoint(x, ray) })
                .Where(x => x.Intersection != null)
                .OrderBy(x => (x.Intersection.Value - ray.Start).Length())
                .FirstOrDefault();

            if (hit == null) return; // Nothing was clicked

            CreateEntity(hit.Intersection.Value);
        }

        // 2D interaction

        protected override void MouseEnter(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            viewport.Control.Cursor = Cursors.Cross;
        }

        protected override void MouseLeave(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            viewport.Control.Cursor = Cursors.Cross;
        }

        protected override void MouseDown(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;

            _state = EntityState.Moving;
            var loc = SnapIfNeeded(camera.ScreenToWorld(e.X, e.Y));
            _location = camera.GetUnusedCoordinate(_location) + loc;
            Invalidate();
        }

        protected override void MouseUp(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            if (e.Button != MouseButtons.Left) return;
            _state = EntityState.Drawn;
            var loc = SnapIfNeeded(camera.ScreenToWorld(e.X, e.Y));
            _location = camera.GetUnusedCoordinate(_location) + loc;
            Invalidate();
        }

        protected override void MouseMove(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            if (!Control.MouseButtons.HasFlag(MouseButtons.Left)) return;
            if (_state != EntityState.Moving) return;
            var loc = SnapIfNeeded(camera.ScreenToWorld(e.X, e.Y));
            _location = camera.GetUnusedCoordinate(_location) + loc;
            Invalidate();
        }

        protected override void KeyDown(MapViewport viewport, OrthographicCamera camera, ViewportEvent e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    CreateEntity(_location);
                    _state = EntityState.None;
                    Invalidate();
                    break;
                case Keys.Escape:
                    _state = EntityState.None;
                    Invalidate();
                    break;
            }
        }

        private async Task CreateEntity(Vector3 origin, string gd = null)
        {
            if (gd == null) gd = _activeEntity;
            if (gd == null) return;

            var colour = Colour.GetDefaultEntityColour();
            var data = await Document.Environment.GetGameData();
            if (data != null)
            {
                var cls = data.Classes.FirstOrDefault(x => String.Equals(x.Name, gd, StringComparison.InvariantCultureIgnoreCase));
                if (cls != null)
                {
                    var col = cls.Behaviours.Where(x => x.Name == "color").ToArray();
                    if (col.Any()) colour = col[0].GetColour(0);
                }
            }

            var entity = new Primitives.MapObjects.Entity(Document.Map.NumberGenerator.Next("MapObject"))
            {
                Data =
                {
                    new EntityData { Name = gd },
                    new ObjectColor(colour),
                    new Origin(origin),
                }
            };

            var transaction = new Transaction();

            transaction.Add(new Attach(Document.Map.Root.ID, entity));

            if (_selectCreatedEntity)
            {
                transaction.Add(new Deselect(Document.Selection));
                transaction.Add(new Select(entity.FindAll()));
            }

            await MapDocumentOperation.Perform(Document, transaction);

            if (_switchToSelectAfterCreation)
            {
                Oy.Publish("ActivateTool", "SelectTool");
            }

            if (_resetEntityTypeOnCreation)
            {
                Oy.Publish("EntityTool:ResetEntityType", this);
            }
        }

        // Rendering

        public override void Render(BufferBuilder builder)
        {
            if (_state != EntityState.None)
            {
                var vec = _location;
                var high = 1024f * 1024f;
                var low = -high;

                // Draw a box around the point
                var c = new Box(vec - Vector3.One * 10, vec + Vector3.One * 10);

                const uint numVertices = 4 * 6 + 6;
                const uint numWireframeIndices = numVertices * 2;

                var points = new VertexStandard[numVertices];
                var indices = new uint[numWireframeIndices];

                var colour = new Vector4(0, 1, 0, 1);

                var vi = 0u;
                var wi = 0u;
                foreach (var face in c.GetBoxFaces())
                {
                    var offs = vi;

                    foreach (var v in face)
                    {
                        points[vi++] = new VertexStandard { 
                            Position = v,
                            Colour = colour,
                            Tint = Vector4.One
                        };
                    }

                    // Lines - [0 1] ... [n-1 n] [n 0]
                    for (uint i = 0; i < 4; i++)
                    {
                        indices[wi++] = offs + i;
                        indices[wi++] = offs + (i == 4 - 1 ? 0 : i + 1);
                    }
                }

                // Draw 3 lines pinpointing the point
                var lineOffset = vi;

                points[vi++] = new VertexStandard { Position = new Vector3(low , vec.Y, vec.Z), Colour = colour, Tint = Vector4.One };
                points[vi++] = new VertexStandard { Position = new Vector3(high, vec.Y, vec.Z), Colour = colour, Tint = Vector4.One };
                points[vi++] = new VertexStandard { Position = new Vector3(vec.X, low , vec.Z), Colour = colour, Tint = Vector4.One };
                points[vi++] = new VertexStandard { Position = new Vector3(vec.X, high, vec.Z), Colour = colour, Tint = Vector4.One };
                points[vi++] = new VertexStandard { Position = new Vector3(vec.X, vec.Y, low ), Colour = colour, Tint = Vector4.One };
                points[vi++] = new VertexStandard { Position = new Vector3(vec.X, vec.Y, high), Colour = colour, Tint = Vector4.One };

                indices[wi++] = lineOffset++;
                indices[wi++] = lineOffset++;
                indices[wi++] = lineOffset++;
                indices[wi++] = lineOffset++;
                indices[wi++] = lineOffset++;
                indices[wi++] = lineOffset++;
                
                var groups = new[]
                {
                    new BufferGroup(PipelineType.WireframeGeneric, CameraType.Both, false, vec, 0, numWireframeIndices)
                };

                builder.Append(points, indices, groups);
            }

            base.Render(builder);
        }
    }
}