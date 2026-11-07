# Quickstart team ingress selection

Keep the chart default ingress class for existing installations. The quickstart-to-team
website steps explicitly select `ingress.className=traefik` for the quickstart cluster.
For another cluster, the operator selects its actual ingress class. Leaving the class
empty globally was rejected because it changes existing installations on upgrade and
clusters with multiple controllers may route differently.

The install proof upgrades the real quickstart release with ingress enabled, a local
TLS Secret and the quickstart controller class. It fetches the frontend and API through
HTTPS on the node ingress, rather than a service port-forward which bypasses routing.
