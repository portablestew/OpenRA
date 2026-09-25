#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public abstract class RenderPostProcessPassBase : IRenderPostProcessPass, INotifyActorDisposing
	{
		readonly Renderer renderer;
		readonly IShader shader;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;
		readonly PostProcessPassType type;

		protected RenderPostProcessPassBase(string name, PostProcessPassType type)
		{
			this.type = type;
			renderer = Game.Renderer;

			// A headless world (no renderer) still constructs the world actor's traits. These passes are
			// purely visual and are only ever drawn through a WorldRenderer, which does not exist headlessly,
			// so leave the GPU resources uncreated rather than failing construction. Game.Renderer is never
			// null in normal play, so this changes nothing there.
			if (renderer == null)
				return;

			shader = renderer.CreateShader(new RenderPostProcessPassShaderBindings(name));
			var vertices = new RenderPostProcessPassVertex[]
			{
				new(-1, -1),
				new(1, -1),
				new(1, 1),
				new(1, 1),
				new(-1, 1),
				new(-1, -1)
			};

			buffer = renderer.CreateVertexBuffer(vertices, false);
		}

		PostProcessPassType IRenderPostProcessPass.Type => type;
		bool IRenderPostProcessPass.Enabled => Enabled;
		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			PrepareRender(wr, shader);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		protected abstract bool Enabled { get; }
		protected abstract void PrepareRender(WorldRenderer wr, IShader shader);

		void INotifyActorDisposing.Disposing(Actor self)
		{
			// Null when constructed headlessly; see the constructor.
			buffer?.Dispose();
		}
	}
}
