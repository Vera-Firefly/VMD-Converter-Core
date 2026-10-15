#include "../third_party/ufbx/ufbx.h"
#include <string.h>
#include <stdio.h>

#if defined(_WIN32)
#define HPS_EXPORT __declspec(dllexport)
#else
#define HPS_EXPORT __attribute__((visibility("default")))
#endif

typedef struct hps_node {
    int parent;
    int is_bone;
    double px, py, pz;
    double qx, qy, qz, qw;
    double sx, sy, sz;
} hps_node;

HPS_EXPORT void *hps_fbx_load(const void *data, size_t size, char *message, size_t capacity)
{
    ufbx_load_opts opts = {0};
    opts.ignore_geometry = true;
    opts.ignore_animation = true;
    opts.target_axes = ufbx_axes_left_handed_y_up;
    opts.target_unit_meters = 1.0;
    // Bake coordinate/units conversion into node transforms. Keeping the
    // default synthetic root reflection would look like a negative Avatar
    // ancestor scale to clients that do not have Unity importer metadata.
    opts.space_conversion = UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY;
    opts.handedness_conversion_axis = UFBX_MIRROR_AXIS_X;
    opts.temp_allocator.memory_limit = 512u * 1024u * 1024u;
    opts.result_allocator.memory_limit = 512u * 1024u * 1024u;
    ufbx_error error;
    ufbx_scene *scene = ufbx_load_memory(data, size, &opts, &error);
    if (!scene && message && capacity) ufbx_format_error(message, capacity, &error);
    return scene;
}
HPS_EXPORT void hps_fbx_free(ufbx_scene *scene) { ufbx_free_scene(scene); }
HPS_EXPORT int hps_fbx_count(const ufbx_scene *scene) { return (int)scene->nodes.count; }
HPS_EXPORT const char *hps_fbx_name(const ufbx_scene *scene, int index)
{
    if (index < 0 || (size_t)index >= scene->nodes.count) return NULL;
    return scene->nodes.data[index]->name.data;
}
HPS_EXPORT int hps_fbx_node(const ufbx_scene *scene, int index, hps_node *out)
{
    if (index < 0 || (size_t)index >= scene->nodes.count || !out) return 0;
    ufbx_node *node = scene->nodes.data[index];
    ufbx_transform t = ufbx_matrix_to_transform(&node->node_to_world);
    out->parent = node->parent ? (int)node->parent->typed_id : -1;
    out->is_bone = node->bone != NULL;
    out->px=t.translation.x; out->py=t.translation.y; out->pz=t.translation.z;
    out->qx=t.rotation.x; out->qy=t.rotation.y; out->qz=t.rotation.z; out->qw=t.rotation.w;
    out->sx=t.scale.x; out->sy=t.scale.y; out->sz=t.scale.z;
    return 1;
}
HPS_EXPORT int hps_fbx_morph_count(const ufbx_scene *scene) { return (int)scene->blend_channels.count; }
HPS_EXPORT const char *hps_fbx_morph_name(const ufbx_scene *scene, int index)
{
    if (index < 0 || (size_t)index >= scene->blend_channels.count) return NULL;
    return scene->blend_channels.data[index]->name.data;
}
HPS_EXPORT int hps_fbx_morph_node(const ufbx_scene *scene, int index)
{
    if (index < 0 || (size_t)index >= scene->blend_channels.count) return -1;
    ufbx_blend_channel *channel = scene->blend_channels.data[index];
    for (size_t n=0; n<scene->nodes.count; n++) {
        ufbx_mesh *mesh = scene->nodes.data[n]->mesh;
        if (!mesh) continue;
        for (size_t d=0; d<mesh->blend_deformers.count; d++) {
            ufbx_blend_deformer *deformer = mesh->blend_deformers.data[d];
            for (size_t c=0; c<deformer->channels.count; c++)
                if (deformer->channels.data[c] == channel) return (int)n;
        }
    }
    return -1;
}
