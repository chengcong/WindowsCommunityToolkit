﻿// Copyright(c) Microsoft Corporation.All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Numerics;
using WinCompData.Tools;

namespace WinCompData.CodeGen
{
    /// <summary>
    /// Optimizes a <see cref="Visual"/> tree by combining and removing containers.
    /// </summary>
    static class TreeReducer
    {
        internal static Visual OptimizeContainers(Visual root)
        {
            var graph = Graph.FromCompositionObject(root, includeVertices: true);
            SimplifyProperties(graph);
            CoalesceContainerShapes(graph);
            CoalesceContainerVisuals(graph);
            return root;
        }

        // Where possible, replace properties with a TransformMatrix.
        static void SimplifyProperties(ObjectGraph<Graph.Node> graph)
        {
            foreach (var (_, obj) in graph.CompositionObjectNodes)
            {
                switch (obj.Type)
                {
                    case CompositionObjectType.ContainerVisual:
                    case CompositionObjectType.ShapeVisual:
                        SimplifyProperties((Visual)obj);
                        break;
                    case CompositionObjectType.CompositionContainerShape:
                    case CompositionObjectType.CompositionSpriteShape:
                        SimplifyProperties((CompositionShape)obj);
                        break;
                }
            }
        }

        // Remove the centerpoint property if it's redundant, and convert properties to TransformMatrix if possible.
        static void SimplifyProperties(Visual obj)
        {
            if (obj.CenterPoint.HasValue &&
                !obj.Scale.HasValue &&
                !obj.RotationAngleInDegrees.HasValue &&
                !obj.Animators.Where(a => a.AnimatedProperty == nameof(obj.Scale) || a.AnimatedProperty == nameof(obj.RotationAngleInDegrees)).Any())
            {
                // Centerpoint is not needed if Scale or Rotation are not used.
                obj.CenterPoint = null;
            }

            // Convert the properties to a transform matrix. This can reduce the
            // number of calls needed to initialize the object, and makes finding
            // and removing redundant containers easier.

            // We currently only support rotation around the Z axis here. Check for that.
            var hasNonStandardRotation =
                obj.RotationAngleInDegrees.HasValue && obj.RotationAngleInDegrees.Value != 0 &&
                obj.RotationAxis.HasValue && obj.RotationAxis != Vector3.UnitZ;

            if (!obj.Animators.Any() && !hasNonStandardRotation)
            {
                // Get the values of the properties, and the defaults for properties that are not set.
                var centerPoint = obj.CenterPoint ?? Vector3.Zero;
                var scale = obj.Scale ?? Vector3.One;
                var rotation = obj.RotationAngleInDegrees ?? 0;
                var offset = obj.Offset ?? Vector3.Zero;
                var transform = obj.TransformMatrix ?? Matrix4x4.Identity;

                // Clear out the properties.
                obj.CenterPoint = null;
                obj.Scale = null;
                obj.RotationAngleInDegrees = null;
                obj.Offset = null;
                obj.TransformMatrix = null;

                // Calculate the matrix that is equivalent to the properties.
                var combinedMatrix =
                    Matrix4x4.CreateScale(scale, centerPoint) *
                    Matrix4x4.CreateRotationZ(DegreesToRadians(rotation), centerPoint) *
                    Matrix4x4.CreateTranslation(offset) *
                    transform;

                // If the matrix actually does something, set it.
                if (!combinedMatrix.IsIdentity)
                {
                    obj.TransformMatrix = combinedMatrix;
                }
            }
        }

        // Remove the centerpoint property if it's redundant, and convert properties to TransformMatrix if possible.
        static void SimplifyProperties(CompositionShape obj)
        {
            // Remove the centerpoint if it's not used by Scale or Rotation.
            if (obj.CenterPoint.HasValue &&
                !obj.Scale.HasValue &&
                !obj.RotationAngleInDegrees.HasValue &&
                !obj.Animators.Where(a => a.AnimatedProperty == nameof(obj.Scale) || a.AnimatedProperty == nameof(obj.RotationAngleInDegrees)).Any())
            {
                // Centerpoint is not needed if Scale or Rotation are not used.
                obj.CenterPoint = null;
            }

            // Convert the properties to a transform matrix. This can reduce the
            // number of calls needed to initialize the object, and makes finding
            // and removing redundant containers easier.
            if (!obj.Animators.Any())
            {
                // Get the values for the properties, and the defaults for the properties that are not set.
                var centerPoint = obj.CenterPoint ?? Vector2.Zero;
                var scale = obj.Scale ?? Vector2.One;
                var rotation = obj.RotationAngleInDegrees ?? 0;
                var offset = obj.Offset ?? Vector2.Zero;
                var transform = obj.TransformMatrix ?? Matrix3x2.Identity;

                // Clear out the properties.
                obj.CenterPoint = null;
                obj.Scale = null;
                obj.RotationAngleInDegrees = null;
                obj.Offset = null;
                obj.TransformMatrix = null;

                // Calculate the matrix that is equivalent to the properties.
                var combinedMatrix =
                    Matrix3x2.CreateScale(scale, centerPoint) *
                    Matrix3x2.CreateRotation(DegreesToRadians(rotation), centerPoint) *
                    Matrix3x2.CreateTranslation(offset) *
                    transform;

                // If the matrix actually does something, set it.
                if (!combinedMatrix.IsIdentity)
                {
                    obj.TransformMatrix = combinedMatrix;
                }
            }
        }

        static float DegreesToRadians(float angle) => (float)(Math.PI * angle / 180.0);


        static void CoalesceContainerShapes(ObjectGraph<Graph.Node> graph)
        {
            var containerShapes = graph.CompositionObjectNodes.Where(n => n.Object.Type == CompositionObjectType.CompositionContainerShape).ToArray();

            // If a container is not animated and has no other properties set apart from a transform,
            // and all of its children are also not animated and have no other properties set apart
            // from a transform, the transform can be pushed down to the child, allowing the parent to be removed.
            var elidableContainers = containerShapes.Where(n =>
            {
                var container = (CompositionContainerShape)n.Object;
                if (container.Properties.PropertyNames.Any() ||
                    container.Animators.Any() ||
                    container.CenterPoint != null ||
                    container.Offset != null ||
                    container.RotationAngleInDegrees != null ||
                    container.Scale != null)
                {
                    return false;
                }

                foreach (var child in container.Shapes)
                {
                    if (child.Properties.PropertyNames.Any() ||
                        child.Animators.Any() ||
                        child.CenterPoint != null ||
                        child.Offset != null ||
                        child.RotationAngleInDegrees != null ||
                        child.Scale != null)
                    {
                        return false;
                    }
                }
                return true;
            });

            // Push the transform down to the child.
            foreach (var node in elidableContainers)
            {
                var container = (CompositionContainerShape)node.Object;
                foreach (var child in container.Shapes)
                {
                    // Push the transform down to the child
                    if (container.TransformMatrix.HasValue)
                    {
                        child.TransformMatrix = (child.TransformMatrix?? Matrix3x2.Identity) * container.TransformMatrix;
                    }
                }
                // Remove the transform from the container.
                container.TransformMatrix = null;
            }

            // If a container is not animated and has no properties set, its children can be inserted into its parent.
            var containersWithNoPropertiesSet = containerShapes.Where(n =>
            {
                var container = (CompositionContainerShape)n.Object;
                if (container.Type != CompositionObjectType.CompositionContainerShape ||
                    container.CenterPoint != null ||
                    container.Offset != null ||
                    container.RotationAngleInDegrees != null ||
                    container.Scale != null ||
                    container.TransformMatrix != null ||
                    container.Animators.Any() ||
                    container.Properties.PropertyNames.Any())
                {
                    return false;
                }
                // Container has no properties set.
                return true;
            }).Select(n => (CompositionContainerShape)n.Object);

            foreach (var container in containersWithNoPropertiesSet)
            {
                // Insert the children into the parent.
                var parent = container.Parent;
                if (parent == null)
                {
                    // The container may have already been removed, or it might be a root.
                    continue;
                }

                // Find the index in the parent of the container.
                // If childCount is 1, just replace the the container in the parent.
                // If childCount is >1, insert into the parent.
                var index = parent.Shapes.IndexOf(container);

                // Get the children from the container.
                var children = container.Shapes.ToArray();
                if (children.Length == 0)
                {
                    // The container has no children. This is rare but can happen if
                    // the container is for a layer type that we don't support.
                    continue;
                }

                // Remove the children from the container.
                container.Shapes.Clear();

                // Insert the first child where the container was.
                parent.Shapes[index] = children[0];

                // Insert the rest of the children.
                for (var i = 1; i < children.Length; i++)
                {
                    parent.Shapes.Insert(index + i, children[i]);
                }
            }
        }

        static void CoalesceContainerVisuals(ObjectGraph<Graph.Node> graph)
        {
            // If a container is not animated and has no properties set, its children can be inserted into its parent.
            var containersWithNoPropertiesSet = graph.CompositionObjectNodes.Where(n =>
            {
                // Find the ContainerVisuals that have no properties set.
                return
                    n.Object is ContainerVisual container &&
                    container.CenterPoint == null &&
                    container.Clip == null &&
                    container.Offset == null &&
                    container.Opacity == null &&
                    container.RotationAngleInDegrees == null &&
                    container.Scale == null &&
                    container.Size == null &&
                    container.TransformMatrix == null &&
                    !container.Animators.Any() &&
                    !container.Properties.PropertyNames.Any();
            }).Select(n => (ContainerVisual)n.Object).ToArray();

            // Pull the children of the container into the parent of the container. Remove the unnecessary containers.
            foreach (var container in containersWithNoPropertiesSet)
            {
                // Insert the children into the parent.
                var parent = container.Parent;
                if (parent == null)
                {
                    // The container may have already been removed, or it might be a root.
                    continue;
                }

                // Find the index in the parent of the container.
                // If childCount is 1, just replace the the container in the parent.
                // If childCount is >1, insert into the parent.
                var index = parent.Children.IndexOf(container);

                // Get the children from the container.
                var children = container.Children.ToArray();
                if (children.Length == 0)
                {
                    // The container has no children. This is rare but can happen if
                    // the container is for a layer type that we don't support.
                    continue;
                }

                // Remove the children from the container.
                container.Children.Clear();
                // Insert the first child where the container was.
                parent.Children[index] = children[0];

                // Insert the rest of the children.
                for (var i = 1; i < children.Length; i++)
                {
                    parent.Children.Insert(index + i, children[i]);
                }
            }
        }
    }
}
