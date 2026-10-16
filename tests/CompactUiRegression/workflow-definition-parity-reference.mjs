import { validateWorkflow, validateParameters, resolveBindings, definitionHash } from '../../modules/desktop-ui-mcp/workflow-definition.mjs';

// Synthetic contracts and runtime values exercise the reference validator only, without authoring models or installed applications.
export function workflowDefinitionFixtures(corpus) {
    return corpus.cases.map(fixture => {
        try {
            const definition = validateWorkflow(fixture.definition, reference => fixture.functions[reference.application + '/' + reference.name]);
            let reply = { hash: definitionHash(definition), definition };
            if (Object.hasOwn(fixture, 'parameters')) {
                validateParameters(definition.inputs, fixture.parameters, fixture.allow_missing ?? false);
                reply = { hash: definitionHash(definition), parameters_valid: true };
                if (fixture.resolve_stage) {
                    const stage = definition.stages.find(stage => stage.id === fixture.resolve_stage);
                    reply.bindings = resolveBindings(stage.bindings, fixture.parameters, fixture.confirmed);
                }
            }
            return { name: fixture.name, reply };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        }
    });
}
